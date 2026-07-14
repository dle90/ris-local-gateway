using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Medisync.RisLocalGateway.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Medisync.RisLocalGateway.Dicom.Stats;

/// <summary>
/// Thống kê LOCAL các study đã gửi ảnh tới gateway — SQLite tại
/// %ProgramData%\Medisync\RisLocalGateway\stats\gateway-stats.db, WAL mode để Service ghi
/// trong khi ConfigApp đọc (2 process). KHÔNG đồng bộ lên HIS.
///
/// Nguyên tắc: ghi stats là BEST-EFFORT, tuyệt đối không được làm hỏng pipeline ảnh —
/// <see cref="TryRecord"/> chỉ đẩy event vào Channel in-memory (non-blocking, đầy thì drop),
/// 1 consumer nền gom batch ghi DB; mọi lỗi DB chỉ log warning.
///
/// Đếm theo TRẠNG THÁI từng instance (PENDING/PUSHED/FAILED), không cộng counter:
///   • Nhận (C-STORE)       → upsert study + instance PENDING (re-send trùng SOP → đè, không đếm trùng)
///   • Đẩy PACS OK          → instance PUSHED (kể cả re-drive dead-letter từ ConfigApp)
///   • Vào dead-letter      → instance FAILED + lý do
/// Event Pushed/Failed tự "chữa" row thiếu (file leftover từ trước khi có feature) bằng upsert.
///
/// Retention: xoá study (kèm instance) cũ hơn <see cref="RetentionDays"/> ngày — CHỈ Service
/// purge (lúc service start + mỗi 24h trong consumer); ConfigApp mở store với enablePurge=false
/// (chỉ đọc + ghi event re-drive, không dọn dữ liệu).
/// </summary>
public sealed class StudyStatsStore : IDisposable
{
    /// <summary>Giữ dữ liệu thống kê 90 ngày, cũ hơn bị purge.</summary>
    public const int RetentionDays = 90;

    private const string DbFileName = "gateway-stats.db";
    private const int MaxQueueLength = 10_000;
    private const int MaxBatch = 256;
    private const int MaxQueryRows = 5_000;
    private const int MaxTextLength = 200; // clamp metadata (tránh dataset rác phình DB)

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(24);

    private readonly ILogger<StudyStatsStore> _logger;
    private readonly Channel<StudyStatsEvent> _channel;
    private readonly SqliteConnection? _writeConn;
    private readonly Task? _consumer;
    private readonly string _dbPath;
    private readonly string? _initError;
    private readonly bool _enablePurge;

    private DateTimeOffset _nextPurgeUtc;
    private long _dropped;

    /// <param name="enablePurge">
    /// true (Service): purge dữ liệu quá <see cref="RetentionDays"/> ngày lúc mở + mỗi 24h.
    /// false (ConfigApp): không bao giờ purge — việc dọn là của Service.
    /// </param>
    public StudyStatsStore(ILogger<StudyStatsStore> logger, bool enablePurge = true)
    {
        _logger = logger;
        _enablePurge = enablePurge;
        _dbPath = Path.Combine(ConfigPaths.StatsDirectory, DbFileName);
        _channel = Channel.CreateBounded<StudyStatsEvent>(new BoundedChannelOptions(MaxQueueLength)
        {
            FullMode = BoundedChannelFullMode.DropWrite, // đầy → TryWrite trả false, drop (ưu tiên pipeline ảnh)
            SingleReader = true,
        });

        try
        {
            Directory.CreateDirectory(ConfigPaths.StatsDirectory);
            _writeConn = OpenConnection();
            EnsureSchema(_writeConn);
            if (_enablePurge) Purge(_writeConn);
            _nextPurgeUtc = DateTimeOffset.UtcNow + PurgeInterval;
            _consumer = Task.Run(ConsumeAsync);
            _logger.LogInformation("StudyStatsStore mở OK: {Path} (retention {Days} ngày, purge={Purge})",
                _dbPath, RetentionDays, _enablePurge);
        }
        catch (Exception ex)
        {
            // Store hỏng (đĩa/quyền) → chạy chế độ TẮT: TryRecord no-op, Query báo lỗi rõ ràng.
            _initError = "Không mở được DB thống kê " + _dbPath + ": " + ex.Message;
            _logger.LogError(ex, "StudyStatsStore init lỗi — thống kê study bị TẮT (pipeline ảnh không ảnh hưởng)");
            _writeConn?.Dispose();
            _writeConn = null;
        }
    }

    /// <summary>Null = store hoạt động; khác null = lý do store bị tắt (hiện lên UI).</summary>
    public string? InitError => _initError;

    // ---------- Ghi (best-effort, non-blocking) ----------

    /// <summary>
    /// Ghi nhận 1 sự kiện — chỉ đẩy vào queue in-memory, KHÔNG chạm DB trên thread gọi.
    /// Nhận null (FromDataset thiếu SOP UID) hoặc store tắt → bỏ qua im lặng.
    /// </summary>
    public void TryRecord(StudyStatsEvent? ev)
    {
        if (ev is null || _writeConn is null) return;
        if (!_channel.Writer.TryWrite(ev))
        {
            var n = Interlocked.Increment(ref _dropped);
            if (n % 1000 == 1)
                _logger.LogWarning("Stats queue đầy — đã drop {N} event (DB chậm/kẹt?)", n);
        }
    }

    private async Task ConsumeAsync()
    {
        var batch = new List<StudyStatsEvent>(MaxBatch);
        try
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < MaxBatch && _channel.Reader.TryRead(out var ev))
                    batch.Add(ev);

                try { ApplyBatch(batch); }
                catch (Exception ex) { _logger.LogWarning(ex, "Ghi batch stats lỗi ({N} event, bỏ qua)", batch.Count); }

                if (_enablePurge && DateTimeOffset.UtcNow >= _nextPurgeUtc)
                {
                    _nextPurgeUtc = DateTimeOffset.UtcNow + PurgeInterval;
                    try { Purge(_writeConn!); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Purge stats lỗi (bỏ qua)"); }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stats consumer dừng bất thường — thống kê ngừng ghi tới khi restart");
        }
    }

    private void ApplyBatch(List<StudyStatsEvent> batch)
    {
        if (batch.Count == 0 || _writeConn is null) return;
        using var tx = _writeConn.BeginTransaction();
        foreach (var ev in batch)
        {
            if (!string.IsNullOrEmpty(ev.StudyUid)) UpsertStudy(_writeConn, tx, ev);
            UpsertInstance(_writeConn, tx, ev);
        }
        tx.Commit();
    }

    private static void UpsertStudy(SqliteConnection conn, SqliteTransaction tx, StudyStatsEvent ev)
    {
        // Received: cập nhật first/last received (MIN/MAX). Pushed/Failed chỉ là self-heal cho row
        // thiếu (leftover cũ) → khi row ĐÃ có thì không đụng timestamp, chỉ điền metadata còn trống.
        var bumpTimestamps = ev.Kind == StudyStatsEventKind.Received
            ? """
              first_received_at_utc = MIN(study.first_received_at_utc, excluded.first_received_at_utc),
              last_received_at_utc  = MAX(study.last_received_at_utc,  excluded.last_received_at_utc),
              """
            : string.Empty;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            INSERT INTO study (study_uid, accession_number, patient_id, patient_name,
                               study_description, modality, calling_ae,
                               first_received_at_utc, last_received_at_utc)
            VALUES (@study, @acc, @pid, @pname, @desc, @mod, @ae, @at, @at)
            ON CONFLICT(study_uid) DO UPDATE SET
              {bumpTimestamps}
              accession_number  = CASE WHEN excluded.accession_number  <> '' THEN excluded.accession_number  ELSE study.accession_number  END,
              patient_id        = CASE WHEN excluded.patient_id        <> '' THEN excluded.patient_id        ELSE study.patient_id        END,
              patient_name      = CASE WHEN excluded.patient_name      <> '' THEN excluded.patient_name      ELSE study.patient_name      END,
              study_description = CASE WHEN excluded.study_description <> '' THEN excluded.study_description ELSE study.study_description END,
              modality          = CASE WHEN excluded.modality          <> '' THEN excluded.modality          ELSE study.modality          END,
              calling_ae        = CASE WHEN excluded.calling_ae        <> '' THEN excluded.calling_ae        ELSE study.calling_ae        END
            """;
        cmd.Parameters.AddWithValue("@study", ev.StudyUid);
        cmd.Parameters.AddWithValue("@acc", Clamp(ev.AccessionNumber));
        cmd.Parameters.AddWithValue("@pid", Clamp(ev.PatientId));
        cmd.Parameters.AddWithValue("@pname", Clamp(ev.PatientName));
        cmd.Parameters.AddWithValue("@desc", Clamp(ev.StudyDescription));
        cmd.Parameters.AddWithValue("@mod", Clamp(ev.Modality));
        cmd.Parameters.AddWithValue("@ae", Clamp(ev.CallingAe));
        cmd.Parameters.AddWithValue("@at", ToUtcText(ev.At));
        cmd.ExecuteNonQuery();
    }

    private static void UpsertInstance(SqliteConnection conn, SqliteTransaction tx, StudyStatsEvent ev)
    {
        var status = ev.Kind switch
        {
            StudyStatsEventKind.Received => "PENDING",
            StudyStatsEventKind.Pushed => "PUSHED",
            _ => "FAILED",
        };

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // study_uid rỗng (vd file hỏng không đọc được dataset) không được đè study_uid đã biết.
        cmd.CommandText = """
            INSERT INTO instance (sop_uid, study_uid, status, failed_reason, updated_at_utc)
            VALUES (@sop, @study, @status, @reason, @at)
            ON CONFLICT(sop_uid) DO UPDATE SET
              study_uid      = CASE WHEN excluded.study_uid <> '' THEN excluded.study_uid ELSE instance.study_uid END,
              status         = excluded.status,
              failed_reason  = excluded.failed_reason,
              updated_at_utc = excluded.updated_at_utc
            """;
        cmd.Parameters.AddWithValue("@sop", ev.SopUid);
        cmd.Parameters.AddWithValue("@study", ev.StudyUid);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@reason", (object?)Clamp(ev.FailedReason ?? string.Empty) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@at", ToUtcText(ev.At));
        cmd.ExecuteNonQuery();
    }

    // ---------- Đọc (tab "Study đã nhận") ----------

    /// <summary>
    /// Danh sách study có ảnh nhận LẦN ĐẦU trong [fromLocal, toExclusiveLocal), mới nhất trước.
    /// Mở connection riêng mỗi lần gọi (WAL cho phép đọc song song với writer). Gọi từ background
    /// thread (Task.Run) — có thể block bởi busy_timeout.
    /// </summary>
    public IReadOnlyList<StudyStatsRow> QueryStudies(DateTimeOffset fromLocal, DateTimeOffset toExclusiveLocal)
    {
        if (_initError is not null) throw new InvalidOperationException(_initError);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT s.study_uid, s.accession_number, s.patient_id, s.patient_name,
                   s.study_description, s.modality, s.calling_ae,
                   s.first_received_at_utc, s.last_received_at_utc,
                   COUNT(i.sop_uid),
                   COALESCE(SUM(CASE WHEN i.status = 'PUSHED' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN i.status = 'FAILED' THEN 1 ELSE 0 END), 0)
            FROM study s
            LEFT JOIN instance i ON i.study_uid = s.study_uid
            WHERE s.first_received_at_utc >= @from AND s.first_received_at_utc < @to
            GROUP BY s.study_uid
            ORDER BY s.first_received_at_utc DESC
            LIMIT {MaxQueryRows}
            """;
        cmd.Parameters.AddWithValue("@from", ToUtcText(fromLocal));
        cmd.Parameters.AddWithValue("@to", ToUtcText(toExclusiveLocal));

        var rows = new List<StudyStatsRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var received = reader.GetInt32(9);
            var pushed = reader.GetInt32(10);
            var failed = reader.GetInt32(11);
            rows.Add(new StudyStatsRow(
                StudyUid: reader.GetString(0),
                AccessionNumber: reader.GetString(1),
                PatientId: reader.GetString(2),
                PatientName: reader.GetString(3),
                StudyDescription: reader.GetString(4),
                Modality: reader.GetString(5),
                CallingAe: reader.GetString(6),
                FirstReceivedUtc: reader.GetString(7),
                FirstReceivedText: ToLocalText(reader.GetString(7)),
                LastReceivedText: ToLocalText(reader.GetString(8)),
                ReceivedCount: received,
                PushedCount: pushed,
                FailedCount: failed,
                PendingCount: Math.Max(0, received - pushed - failed)));
        }
        return rows;
    }

    // ---------- Hạ tầng ----------

    private SqliteConnection OpenConnection()
    {
        // Pooling=false: đóng là nhả file handle thật (ConfigApp mở/đóng theo query, tránh giữ khoá DB).
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        // WAL: Service ghi + ConfigApp đọc/ghi đồng thời (2 process, đĩa local).
        // busy_timeout: 2 writer đụng nhau thì chờ thay vì ném SQLITE_BUSY ngay.
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS study (
              study_uid             TEXT PRIMARY KEY,
              accession_number      TEXT NOT NULL DEFAULT '',
              patient_id            TEXT NOT NULL DEFAULT '',
              patient_name          TEXT NOT NULL DEFAULT '',
              study_description     TEXT NOT NULL DEFAULT '',
              modality              TEXT NOT NULL DEFAULT '',
              calling_ae            TEXT NOT NULL DEFAULT '',
              first_received_at_utc TEXT NOT NULL,
              last_received_at_utc  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_study_first_received ON study(first_received_at_utc);
            CREATE TABLE IF NOT EXISTS instance (
              sop_uid        TEXT PRIMARY KEY,
              study_uid      TEXT NOT NULL,
              status         TEXT NOT NULL,
              failed_reason  TEXT NULL,
              updated_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_instance_study ON instance(study_uid);
            """;
        cmd.ExecuteNonQuery();
    }

    private void Purge(SqliteConnection conn)
    {
        var cutoff = ToUtcText(DateTimeOffset.UtcNow.AddDays(-RetentionDays));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM instance WHERE study_uid IN
              (SELECT study_uid FROM study WHERE first_received_at_utc < @cutoff);
            DELETE FROM study WHERE first_received_at_utc < @cutoff;
            -- instance mồ côi (self-heal thiếu study) cũng dọn theo tuổi
            DELETE FROM instance WHERE updated_at_utc < @cutoff
              AND study_uid NOT IN (SELECT study_uid FROM study);
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        var n = cmd.ExecuteNonQuery();
        if (n > 0) _logger.LogInformation("Stats purge: xoá {N} row cũ hơn {Days} ngày", n, RetentionDays);
    }

    /// <summary>ISO-8601 UTC cố định độ dài → so sánh/sort được bằng string trong SQLite.</summary>
    private static string ToUtcText(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>UTC text trong DB → text giờ LOCAL định dạng dd/MM/yyyy HH:mm:ss (24h, không offset).</summary>
    private static string ToLocalText(string utcText)
    {
        if (DateTimeOffset.TryParse(utcText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        return utcText;
    }

    private static string Clamp(string s) => s.Length <= MaxTextLength ? s : s.Substring(0, MaxTextLength);

    public void Dispose()
    {
        try
        {
            _channel.Writer.TryComplete();
            _consumer?.Wait(TimeSpan.FromSeconds(3)); // flush nốt event đang chờ
        }
        catch { /* shutdown — bỏ qua */ }
        _writeConn?.Dispose();
    }
}
