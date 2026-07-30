using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RacinageFreeDesktop {
  internal sealed class ConnectedMessaging : IDisposable {
    private const string ApiRoot = "https://racinage.com/api/messaging/v1";
    private static readonly byte[] TokenEntropy =
      Encoding.UTF8.GetBytes("Racinage Free connected messaging token v1");
    private readonly LocalStore store;
    private readonly ConnectedVault vault;
    private readonly JavaScriptSerializer json =
      new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private Thread worker;
    private volatile bool running;

    internal ConnectedMessaging(LocalStore store) {
      this.store = store;
      vault = new ConnectedVault();
      Initialize();
    }

    private void Initialize() {
      using (SqliteDb db = Open()) {
        db.Exec(
          "CREATE TABLE IF NOT EXISTS connected_account (" +
          "id INTEGER PRIMARY KEY CHECK(id=1),state TEXT NOT NULL DEFAULT 'disconnected'," +
          "user_id TEXT NOT NULL DEFAULT '',access_expires_utc TEXT NOT NULL DEFAULT ''," +
          "refresh_expires_utc TEXT NOT NULL DEFAULT '',event_cursor TEXT NOT NULL DEFAULT ''," +
          "device_code_cipher TEXT NOT NULL DEFAULT '',user_code TEXT NOT NULL DEFAULT ''," +
          "verification_url TEXT NOT NULL DEFAULT '',device_expires_utc TEXT NOT NULL DEFAULT ''," +
          "quota_cipher TEXT NOT NULL DEFAULT '',last_sync_utc TEXT NOT NULL DEFAULT ''," +
          "last_error TEXT NOT NULL DEFAULT '',updated_at TEXT NOT NULL)");
        db.Execute(
          "INSERT OR IGNORE INTO connected_account(id,updated_at)VALUES(1,?)",
          Now());
        db.Exec(
          "CREATE TABLE IF NOT EXISTS connected_conversations (" +
          "long_id TEXT PRIMARY KEY,kind TEXT NOT NULL,state TEXT NOT NULL," +
          "title_cipher TEXT NOT NULL,revision INTEGER NOT NULL DEFAULT 1," +
          "last_sequence INTEGER NOT NULL DEFAULT 0,read_sequence INTEGER NOT NULL DEFAULT 0," +
          "unread_count INTEGER NOT NULL DEFAULT 0,muted INTEGER NOT NULL DEFAULT 0," +
          "archived INTEGER NOT NULL DEFAULT 0,updated_at_utc TEXT NOT NULL)");
        db.Exec(
          "CREATE TABLE IF NOT EXISTS connected_messages (" +
          "long_id TEXT PRIMARY KEY,conversation_long_id TEXT NOT NULL," +
          "sequence_no INTEGER NOT NULL,sender_cipher TEXT NOT NULL,message_type TEXT NOT NULL," +
          "root_message_long_id TEXT NOT NULL DEFAULT '',body_cipher TEXT NOT NULL," +
          "metadata_cipher TEXT NOT NULL,edited_at_utc TEXT NOT NULL DEFAULT ''," +
          "deleted_at_utc TEXT NOT NULL DEFAULT '',created_at_utc TEXT NOT NULL," +
          "UNIQUE(conversation_long_id,sequence_no))");
        db.Exec(
          "CREATE INDEX IF NOT EXISTS idx_connected_messages_conversation " +
          "ON connected_messages(conversation_long_id,sequence_no)");
        db.Exec(
          "CREATE TABLE IF NOT EXISTS connected_outbox (" +
          "id INTEGER PRIMARY KEY AUTOINCREMENT,long_id TEXT NOT NULL UNIQUE," +
          "conversation_long_id TEXT NOT NULL,item_kind TEXT NOT NULL," +
          "payload_cipher TEXT NOT NULL DEFAULT '',encrypted_file_path TEXT NOT NULL DEFAULT ''," +
          "file_name_cipher TEXT NOT NULL DEFAULT '',mime_type TEXT NOT NULL DEFAULT ''," +
          "file_size INTEGER NOT NULL DEFAULT 0,sha256 TEXT NOT NULL DEFAULT ''," +
          "upload_id TEXT NOT NULL DEFAULT '',attachment_id TEXT NOT NULL DEFAULT ''," +
          "upload_offset INTEGER NOT NULL DEFAULT 0,status TEXT NOT NULL DEFAULT 'queued'," +
          "error_code TEXT NOT NULL DEFAULT '',created_at_utc TEXT NOT NULL," +
          "updated_at_utc TEXT NOT NULL)");
        db.Exec(
          "CREATE INDEX IF NOT EXISTS idx_connected_outbox_state " +
          "ON connected_outbox(status,id)");
      }
    }

    internal void Start() {
      if (running) return;
      running = true;
      worker = new Thread(WorkerLoop);
      worker.IsBackground = true;
      worker.Name = "Racinage connected messaging";
      worker.Start();
    }

    internal void Stop() {
      running = false;
      wake.Set();
      try {
        if (worker != null && worker.IsAlive) worker.Join(2000);
      } catch {
      }
    }

    public void Dispose() {
      Stop();
      wake.Dispose();
    }

    internal Dictionary<string, object> Status() {
      using (SqliteDb db = Open()) {
        Dictionary<string, string> state =
          db.QueryOne("SELECT * FROM connected_account WHERE id=1");
        return new Dictionary<string, object> {
          { "state", Value(state, "state") },
          { "connected", HasRefreshToken() },
          { "user_code", Value(state, "user_code") },
          { "verification_url", Value(state, "verification_url") },
          { "device_expires_utc", Value(state, "device_expires_utc") },
          { "last_sync_utc", Value(state, "last_sync_utc") },
          { "last_error", Value(state, "last_error") },
          { "cached_conversations", ToLong(db.Scalar(
            "SELECT COUNT(*) FROM connected_conversations")) },
          { "queued_items", ToLong(db.Scalar(
            "SELECT COUNT(*) FROM connected_outbox WHERE status IN('queued','uploading')")) },
          { "conflicts", ToLong(db.Scalar(
            "SELECT COUNT(*) FROM connected_outbox WHERE status='conflict'")) }
        };
      }
    }

    internal Dictionary<string, object> Action(Dictionary<string, object> request) {
      string action = GetString(request, "action");
      if (action == "device_start") return StartAuthorization();
      if (action == "device_poll") return PollAuthorization();
      if (action == "disconnect") return Disconnect();
      if (action == "sync") {
        wake.Set();
        return new Dictionary<string, object> {
          { "ok", true },
          { "message", "Sync queued." },
          { "status", Status() }
        };
      }
      if (action == "queue_message") return QueueMessage(request);
      throw new InvalidOperationException("This connected messaging action is unavailable.");
    }

    private Dictionary<string, object> StartAuthorization() {
      Dictionary<string, object> body = new Dictionary<string, object> {
        { "stable_device_id", store.DeviceId },
        { "device_name", Environment.MachineName },
        { "client_version", PortablePaths.Version },
        { "scopes", new[] {
          "account.read", "messaging.read", "messaging.write",
          "files.read", "files.write", "events.read"
        } }
      };
      ApiResult result = Call("POST", "/device/authorizations", body, "", "");
      if (result.Status != 201) throw ApiException(result);
      Dictionary<string, object> response = Parse(result.Body);
      string verification = GetString(response, "verification_uri_complete");
      Uri verificationUri;
      if (
        !Uri.TryCreate(verification, UriKind.Absolute, out verificationUri)
        || verificationUri.Scheme != Uri.UriSchemeHttps
        || !verificationUri.Host.Equals("racinage.com", StringComparison.OrdinalIgnoreCase)
        || !verificationUri.AbsolutePath.Equals(
          "/connect-device",
          StringComparison.OrdinalIgnoreCase)
      ) {
        throw new InvalidDataException("The hosted verification address was rejected.");
      }
      int expires = GetInt(response, "expires_in");
      using (SqliteDb db = Open()) {
        db.Execute(
          "UPDATE connected_account SET state='authorizing',device_code_cipher=?," +
          "user_code=?,verification_url=?,device_expires_utc=?,last_error='',updated_at=? " +
          "WHERE id=1",
          vault.EncryptString(GetString(response, "device_code")),
          GetString(response, "user_code"),
          verification,
          DateTime.UtcNow.AddSeconds(Math.Max(60, expires)).ToString(
            "o",
            CultureInfo.InvariantCulture),
          Now());
      }
      Dictionary<string, object> status = Status();
      status["ok"] = true;
      status["message"] =
        "Continue in your browser. Password and two-factor authentication stay on racinage.com.";
      return status;
    }

    private Dictionary<string, object> PollAuthorization() {
      Dictionary<string, string> state;
      using (SqliteDb db = Open()) {
        state = db.QueryOne("SELECT * FROM connected_account WHERE id=1");
      }
      string cipher = Value(state, "device_code_cipher");
      if (cipher == "") throw new InvalidOperationException("Start a device connection first.");
      if (ParseUtc(Value(state, "device_expires_utc")) <= DateTime.UtcNow) {
        ClearDeviceAuthorization("The device code expired.");
        throw new InvalidOperationException("The device code expired.");
      }
      ApiResult result = Call(
        "POST",
        "/device/token",
        new Dictionary<string, object> {
          { "device_code", vault.DecryptString(cipher) }
        },
        "",
        "");
      if (result.Status == 428) {
        return new Dictionary<string, object> {
          { "ok", true },
          { "pending", true },
          { "message", "Waiting for browser approval." },
          { "status", Status() }
        };
      }
      if (result.Status != 200 && result.Status != 201) {
        ClearDeviceAuthorization(ApiError(result));
        throw ApiException(result);
      }
      SaveTokenPair(Parse(result.Body));
      using (SqliteDb db = Open()) {
        db.Execute(
          "UPDATE connected_account SET state='connected',device_code_cipher=''," +
          "user_code='',verification_url='',device_expires_utc='',last_error='',updated_at=? " +
          "WHERE id=1",
          Now());
      }
      wake.Set();
      return new Dictionary<string, object> {
        { "ok", true },
        { "pending", false },
        { "message", "Racinage account connected." },
        { "status", Status() }
      };
    }

    private Dictionary<string, object> Disconnect() {
      string refresh = ReadProtectedToken("connected-refresh.token");
      if (refresh != "") {
        try {
          Call(
            "POST",
            "/token/revoke",
            new Dictionary<string, object> { { "refresh_token", refresh } },
            "",
            "");
        } catch {
        }
      }
      DeleteToken("connected-access.token");
      DeleteToken("connected-refresh.token");
      using (SqliteDb db = Open()) {
        db.Execute(
          "UPDATE connected_account SET state='disconnected',user_id=''," +
          "access_expires_utc='',refresh_expires_utc='',event_cursor=''," +
          "device_code_cipher='',user_code='',verification_url='',device_expires_utc=''," +
          "last_error='',updated_at=? WHERE id=1",
          Now());
      }
      return new Dictionary<string, object> {
        { "ok", true },
        { "message", "Connected account removed from this Windows profile." },
        { "status", Status() }
      };
    }

    internal Dictionary<string, object> QueueFile(
      Stream input,
      long contentLength,
      string conversationLongId,
      string encodedName,
      string mimeType
    ) {
      RequireConversation(conversationLongId);
      if (contentLength < 1 || contentLength > 209715200L) {
        throw new InvalidDataException("The queued file size is not allowed.");
      }
      string name = Uri.UnescapeDataString(encodedName ?? "").Trim();
      name = Path.GetFileName(name);
      if (name == "" || name.Length > 255) {
        throw new InvalidDataException("The queued file name is invalid.");
      }
      string longId = "cof_" + RandomToken(18);
      string queueRoot = Path.Combine(PortablePaths.MediaDir, "connected-outbox");
      Directory.CreateDirectory(queueRoot);
      string encryptedPath = Path.Combine(queueRoot, longId + ".rmsg");
      ConnectedFileInfo encrypted = vault.EncryptFile(
        input,
        contentLength,
        encryptedPath);
      try {
        using (SqliteDb db = Open()) {
          db.Execute(
            "INSERT INTO connected_outbox(" +
            "long_id,conversation_long_id,item_kind,encrypted_file_path,file_name_cipher," +
            "mime_type,file_size,sha256,status,created_at_utc,updated_at_utc)" +
            "VALUES(?,?,'file',?,?,?,?,?,'queued',?,?)",
            longId,
            conversationLongId,
            encryptedPath,
            vault.EncryptString(name),
            (mimeType ?? "application/octet-stream").Substring(
              0,
              Math.Min(100, (mimeType ?? "application/octet-stream").Length)),
            encrypted.Size,
            encrypted.Sha256,
            Now(),
            Now());
        }
      } catch {
        try { File.Delete(encryptedPath); } catch { }
        throw;
      }
      wake.Set();
      return new Dictionary<string, object> {
        { "ok", true },
        { "file_queue_id", longId },
        { "name", name },
        { "bytes", encrypted.Size }
      };
    }

    private Dictionary<string, object> QueueMessage(Dictionary<string, object> request) {
      string conversation = GetString(request, "conversation_id");
      RequireConversation(conversation);
      string body = GetString(request, "body").Trim();
      if (body.Length > 10000) throw new InvalidDataException("The message is too long.");
      List<string> files = GetStrings(request, "file_queue_ids")
        .Where(value => value.StartsWith("cof_", StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal)
        .ToList();
      if (files.Count > 4) throw new InvalidDataException("A message can include four files.");
      if (body == "" && files.Count == 0) {
        throw new InvalidDataException("Write a message or attach a file.");
      }
      using (SqliteDb db = Open()) {
        foreach (string file in files) {
          Dictionary<string, string> row = db.QueryOne(
            "SELECT conversation_long_id,status FROM connected_outbox " +
            "WHERE long_id=? AND item_kind='file' LIMIT 1",
            file);
          if (row == null || Value(row, "conversation_long_id") != conversation) {
            throw new InvalidDataException("A queued file is unavailable.");
          }
        }
        Dictionary<string, object> payload = new Dictionary<string, object> {
          { "body", body },
          { "root_message_id", GetString(request, "root_message_id") },
          { "file_queue_ids", files.ToArray() }
        };
        string longId = "com_" + RandomToken(18);
        db.Execute(
          "INSERT INTO connected_outbox(" +
          "long_id,conversation_long_id,item_kind,payload_cipher,status," +
          "created_at_utc,updated_at_utc)VALUES(?,?,'message',?,'queued',?,?)",
          longId,
          conversation,
          vault.EncryptString(json.Serialize(payload)),
          Now(),
          Now());
      }
      wake.Set();
      return new Dictionary<string, object> {
        { "ok", true },
        { "message", "Message encrypted and queued." },
        { "status", Status() }
      };
    }

    private void WorkerLoop() {
      bool synchronize = true;
      while (running) {
        try {
          if (!HasRefreshToken()) {
            wake.WaitOne(60000);
            synchronize = true;
            continue;
          }
          if (synchronize) {
            Synchronize();
            synchronize = false;
          }
          bool eventSeen = ListenForEvents();
          synchronize = eventSeen || wake.WaitOne(0);
        } catch (Exception error) {
          Program.Log("Connected messaging sync error: " + error.Message);
          SetError(error.Message);
          wake.WaitOne(5000);
          synchronize = true;
        }
      }
    }

    private void Synchronize() {
      string access = EnsureAccessToken();
      ApiResult accountResult = AuthorizedCall("GET", "/account", null, "", access);
      if (accountResult.Status != 200) throw ApiException(accountResult);
      Dictionary<string, object> account = Parse(accountResult.Body);
      string accountState = GetString(account, "state");
      if (accountState != "active") {
        using (SqliteDb db = Open()) {
          db.Execute(
            "UPDATE connected_account SET state='blocked',last_error=?," +
            "quota_cipher=?,updated_at=? WHERE id=1",
            "The hosted account is unavailable.",
            vault.EncryptString(json.Serialize(
              GetObject(account, "quota"))),
            Now());
        }
        return;
      }
      using (SqliteDb db = Open()) {
        db.Execute(
          "UPDATE connected_account SET state='connected',user_id=?,quota_cipher=?," +
          "last_error='',updated_at=? WHERE id=1",
          GetString(account, "id"),
          vault.EncryptString(json.Serialize(GetObject(account, "quota"))),
          Now());
      }
      FlushOutbox();
      SynchronizeConversations();
      using (SqliteDb db = Open()) {
        db.Execute(
          "UPDATE connected_account SET last_sync_utc=?,last_error='',updated_at=? WHERE id=1",
          Now(),
          Now());
      }
    }

    private void FlushOutbox() {
      using (SqliteDb db = Open()) {
        List<Dictionary<string, string> > rows = db.Query(
          "SELECT * FROM connected_outbox WHERE status IN('queued','uploading','uploaded') " +
          "ORDER BY id");
        foreach (Dictionary<string, string> row in rows) {
          if (Value(row, "item_kind") == "file") ProcessFile(db, row);
          else ProcessMessage(db, row);
        }
      }
    }

    private void ProcessFile(SqliteDb db, Dictionary<string, string> row) {
      if (Value(row, "status") == "uploaded") return;
      string access = EnsureAccessToken();
      string uploadId = Value(row, "upload_id");
      long offset = ToLong(Value(row, "upload_offset"));
      if (uploadId == "") {
        Dictionary<string, object> request = new Dictionary<string, object> {
          { "name", vault.DecryptString(Value(row, "file_name_cipher")) },
          { "mime", Value(row, "mime_type") },
          { "size", ToLong(Value(row, "file_size")) },
          { "sha256", Value(row, "sha256") },
          { "voice", false }
        };
        ApiResult started = AuthorizedCall(
          "POST",
          "/conversations/" + Uri.EscapeDataString(
            Value(row, "conversation_long_id")) + "/uploads",
          request,
          Value(row, "long_id"),
          access);
        if (started.Status != 200 && started.Status != 201) {
          MarkConflict(db, row, ApiError(started));
          return;
        }
        Dictionary<string, object> response = Parse(started.Body);
        uploadId = GetString(response, "upload_id");
        offset = GetLong(response, "offset");
        db.Execute(
          "UPDATE connected_outbox SET upload_id=?,upload_offset=?,status='uploading'," +
          "updated_at_utc=? WHERE id=?",
          uploadId,
          offset,
          Now(),
          ToLong(Value(row, "id")));
      }
      using (Stream clear = vault.OpenDecryptedFile(
        Value(row, "encrypted_file_path"))) {
        Skip(clear, offset);
        byte[] buffer = new byte[8 * 1024 * 1024];
        while (offset < ToLong(Value(row, "file_size"))) {
          int wanted = (int)Math.Min(
            buffer.Length,
            ToLong(Value(row, "file_size")) - offset);
          int read = ReadFull(clear, buffer, wanted);
          if (read < 1) {
            MarkConflict(db, row, "local_file_incomplete");
            return;
          }
          ApiResult chunk = AuthorizedBinaryCall(
            "PUT",
            "/uploads/" + Uri.EscapeDataString(uploadId),
            buffer,
            read,
            new Dictionary<string, string> {
              { "Upload-Offset", offset.ToString(CultureInfo.InvariantCulture) }
            },
            EnsureAccessToken());
          if (chunk.Status == 409) {
            Dictionary<string, object> conflict = Parse(chunk.Body);
            long expected = GetLong(conflict, "expected_offset");
            db.Execute(
              "UPDATE connected_outbox SET upload_offset=?,updated_at_utc=? WHERE id=?",
              expected,
              Now(),
              ToLong(Value(row, "id")));
            return;
          }
          if (chunk.Status != 200) {
            MarkConflict(db, row, ApiError(chunk));
            return;
          }
          offset = GetLong(Parse(chunk.Body), "offset");
          db.Execute(
            "UPDATE connected_outbox SET upload_offset=?,updated_at_utc=? WHERE id=?",
            offset,
            Now(),
            ToLong(Value(row, "id")));
        }
      }
      ApiResult completed = AuthorizedCall(
        "POST",
        "/uploads/" + Uri.EscapeDataString(uploadId) + "/complete",
        new Dictionary<string, object>(),
        Value(row, "long_id") + "-complete",
        EnsureAccessToken());
      if (completed.Status != 200) {
        MarkConflict(db, row, ApiError(completed));
        return;
      }
      Dictionary<string, object> complete = Parse(completed.Body);
      db.Execute(
        "UPDATE connected_outbox SET attachment_id=?,status='uploaded',error_code=''," +
        "updated_at_utc=? WHERE id=?",
        GetString(complete, "attachment_id"),
        Now(),
        ToLong(Value(row, "id")));
    }

    private void ProcessMessage(SqliteDb db, Dictionary<string, string> row) {
      Dictionary<string, object> payload = Parse(
        vault.DecryptString(Value(row, "payload_cipher")));
      List<string> fileIds = GetStrings(payload, "file_queue_ids");
      List<string> attachmentIds = new List<string>();
      foreach (string fileId in fileIds) {
        Dictionary<string, string> file = db.QueryOne(
          "SELECT status,attachment_id,error_code,encrypted_file_path FROM connected_outbox " +
          "WHERE long_id=? AND item_kind='file' LIMIT 1",
          fileId);
        if (file == null) {
          MarkConflict(db, row, "queued_file_unavailable");
          return;
        }
        if (Value(file, "status") == "conflict") {
          MarkConflict(db, row, "file_" + Value(file, "error_code"));
          return;
        }
        if (Value(file, "status") != "uploaded") return;
        attachmentIds.Add(Value(file, "attachment_id"));
      }
      Dictionary<string, object> request = new Dictionary<string, object> {
        { "body", GetString(payload, "body") },
        { "root_message_id", GetString(payload, "root_message_id") },
        { "attachment_ids", attachmentIds.ToArray() }
      };
      ApiResult sent = AuthorizedCall(
        "POST",
        "/conversations/" + Uri.EscapeDataString(
          Value(row, "conversation_long_id")) + "/messages",
        request,
        Value(row, "long_id"),
        EnsureAccessToken());
      if (sent.Status != 200 && sent.Status != 201) {
        MarkConflict(db, row, ApiError(sent));
        return;
      }
      db.Execute(
        "UPDATE connected_outbox SET status='sent',error_code='',updated_at_utc=? " +
        "WHERE id=?",
        Now(),
        ToLong(Value(row, "id")));
      foreach (string fileId in fileIds) {
        Dictionary<string, string> file = db.QueryOne(
          "SELECT encrypted_file_path FROM connected_outbox WHERE long_id=? LIMIT 1",
          fileId);
        if (file != null) {
          try { File.Delete(Value(file, "encrypted_file_path")); } catch { }
          db.Execute(
            "UPDATE connected_outbox SET status='sent',encrypted_file_path=''," +
            "updated_at_utc=? WHERE long_id=?",
            Now(),
            fileId);
        }
      }
    }

    private void SynchronizeConversations() {
      ApiResult result = AuthorizedCall(
        "GET",
        "/conversations",
        null,
        "",
        EnsureAccessToken());
      if (result.Status != 200) throw ApiException(result);
      Dictionary<string, object> response = Parse(result.Body);
      foreach (object value in GetList(response, "items")) {
        Dictionary<string, object> conversation = value as Dictionary<string, object>;
        if (conversation == null) continue;
        string longId = GetString(conversation, "id");
        bool refresh;
        using (SqliteDb db = Open()) {
          Dictionary<string, string> existing = db.QueryOne(
            "SELECT revision,last_sequence FROM connected_conversations WHERE long_id=?",
            longId);
          refresh = existing == null
            || ToLong(Value(existing, "revision")) != GetLong(conversation, "revision")
            || ToLong(Value(existing, "last_sequence"))
              != GetLong(conversation, "last_sequence");
          db.Execute(
            "INSERT OR REPLACE INTO connected_conversations(" +
            "long_id,kind,state,title_cipher,revision,last_sequence,read_sequence," +
            "unread_count,muted,archived,updated_at_utc)VALUES(?,?,?,?,?,?,?,?,?,?,?)",
            longId,
            GetString(conversation, "kind"),
            GetString(conversation, "state"),
            vault.EncryptString(GetString(conversation, "title")),
            GetLong(conversation, "revision"),
            GetLong(conversation, "last_sequence"),
            GetLong(conversation, "read_sequence"),
            GetLong(conversation, "unread_count"),
            GetBool(conversation, "muted") ? 1 : 0,
            GetBool(conversation, "archived") ? 1 : 0,
            GetString(conversation, "updated_at_utc"));
        }
        if (refresh) SynchronizeMessages(longId);
      }
    }

    private void SynchronizeMessages(string conversationLongId) {
      long before = 0;
      while (running) {
        string path =
          "/conversations/" + Uri.EscapeDataString(conversationLongId) +
          "/messages?limit=100" +
          (before > 0
            ? "&before_sequence=" + before.ToString(CultureInfo.InvariantCulture)
            : "");
        ApiResult result = AuthorizedCall(
          "GET",
          path,
          null,
          "",
          EnsureAccessToken());
        if (result.Status != 200) throw ApiException(result);
        Dictionary<string, object> response = Parse(result.Body);
        ArrayList items = GetList(response, "items");
        long minimum = 0;
        using (SqliteDb db = Open()) {
          foreach (object value in items) {
            Dictionary<string, object> message = value as Dictionary<string, object>;
            if (message == null) continue;
            long sequence = GetLong(message, "sequence");
            if (minimum == 0 || sequence < minimum) minimum = sequence;
            db.Execute(
              "INSERT OR REPLACE INTO connected_messages(" +
              "long_id,conversation_long_id,sequence_no,sender_cipher,message_type," +
              "root_message_long_id,body_cipher,metadata_cipher,edited_at_utc," +
              "deleted_at_utc,created_at_utc)VALUES(?,?,?,?,?,?,?,?,?,?,?)",
              GetString(message, "id"),
              conversationLongId,
              sequence,
              vault.EncryptString(json.Serialize(GetObject(message, "sender"))),
              GetString(message, "type"),
              GetString(message, "root_message_id"),
              vault.EncryptString(GetString(message, "body")),
              vault.EncryptString(json.Serialize(new Dictionary<string, object> {
                { "attachments", GetValue(message, "attachments") },
                { "reactions", GetValue(message, "reactions") }
              })),
              GetString(message, "edited_at_utc"),
              GetString(message, "deleted_at_utc"),
              GetString(message, "created_at_utc"));
          }
        }
        if (items.Count < 100 || minimum <= 1) break;
        before = minimum;
      }
    }

    private bool ListenForEvents() {
      string cursor = "";
      using (SqliteDb db = Open()) {
        cursor = Value(
          db.QueryOne("SELECT event_cursor FROM connected_account WHERE id=1"),
          "event_cursor");
      }
      string path = "/events/stream" +
        (cursor == "" ? "" : "?cursor=" + Uri.EscapeDataString(cursor));
      HttpWebRequest request = CreateRequest("GET", path);
      request.Accept = "text/event-stream";
      request.Headers[HttpRequestHeader.Authorization] =
        "Bearer " + EnsureAccessToken();
      ApiStreamResult result = OpenStream(request);
      if (result.Status == 401) {
        ExpireAccessToken();
        result.Dispose();
        return true;
      }
      if (result.Status != 200) {
        string error;
        using (result) {
          using (StreamReader reader = new StreamReader(result.Stream, Encoding.UTF8)) {
            error = reader.ReadToEnd();
          }
        }
        throw new InvalidOperationException("SSE connection failed: " + error);
      }
      bool seen = false;
      string nextCursor = "";
      using (result) {
        using (StreamReader reader = new StreamReader(result.Stream, Encoding.UTF8)) {
          while (running) {
            string line = reader.ReadLine();
            if (line == null) break;
            if (line.StartsWith("id:", StringComparison.Ordinal)) {
              nextCursor = line.Substring(3).Trim();
            } else if (line.StartsWith("event:", StringComparison.Ordinal)) {
              seen = true;
            } else if (line == "" && nextCursor != "") {
              using (SqliteDb db = Open()) {
                db.Execute(
                  "UPDATE connected_account SET event_cursor=?,updated_at=? WHERE id=1",
                  nextCursor,
                  Now());
              }
              nextCursor = "";
            }
            if (wake.WaitOne(0)) return true;
          }
        }
      }
      return seen;
    }

    internal string Render(string selectedConversation) {
      Dictionary<string, object> status = Status();
      bool connected = Convert.ToBoolean(status["connected"], CultureInfo.InvariantCulture);
      string state = Convert.ToString(status["state"], CultureInfo.InvariantCulture);
      string userCode = Convert.ToString(status["user_code"], CultureInfo.InvariantCulture);
      StringBuilder conversations = new StringBuilder();
      List<Dictionary<string, string> > conversationRows;
      using (SqliteDb db = Open()) {
        conversationRows = db.Query(
          "SELECT * FROM connected_conversations WHERE archived=0 " +
          "ORDER BY unread_count>0 DESC,updated_at_utc DESC");
      }
      if (
        selectedConversation == null
        || !conversationRows.Any(row => Value(row, "long_id") == selectedConversation)
      ) {
        selectedConversation = conversationRows.Count > 0
          ? Value(conversationRows[0], "long_id")
          : "";
      }
      foreach (Dictionary<string, string> row in conversationRows) {
        string id = Value(row, "long_id");
        string title = SafeDecrypt(Value(row, "title_cipher"), "Conversation");
        conversations.Append(
          "<a class='connected-conversation" +
          (id == selectedConversation ? " active" : "") +
          "' href='/messages?conversation=" + Uri.EscapeDataString(id) + "'>" +
          "<strong>" + H(title) + "</strong><span>" +
          H(Value(row, "kind").Replace('_', ' ')) +
          (ToLong(Value(row, "unread_count")) > 0
            ? " - " + H(Value(row, "unread_count")) + " unread"
            : "") +
          "</span></a>");
      }
      if (conversations.Length == 0) {
        conversations.Append(
          "<p class='empty'>No connected conversations are cached yet.</p>");
      }

      StringBuilder messages = new StringBuilder();
      if (selectedConversation != "") {
        List<Dictionary<string, string> > messageRows;
        using (SqliteDb db = Open()) {
          messageRows = db.Query(
            "SELECT * FROM connected_messages WHERE conversation_long_id=? " +
            "ORDER BY sequence_no",
            selectedConversation);
        }
        foreach (Dictionary<string, string> row in messageRows) {
          Dictionary<string, object> sender = SafeParse(
            SafeDecrypt(Value(row, "sender_cipher"), "{}"));
          string body = SafeDecrypt(Value(row, "body_cipher"), "");
          bool deleted = Value(row, "deleted_at_utc") != "";
          messages.Append(
            "<article class='connected-message'><div><strong>" +
            H(GetString(sender, "name") == "" ? "Deleted User" : GetString(sender, "name")) +
            "</strong><time>" + H(DisplayUtc(Value(row, "created_at_utc"))) +
            "</time></div><p>" +
            H(deleted ? "Message deleted" : body).Replace("\n", "<br>") +
            "</p>" + AttachmentSummary(row) + "</article>");
        }
        if (messages.Length == 0) {
          messages.Append("<p class='empty'>No messages are cached in this conversation.</p>");
        }
      } else {
        messages.Append(
          "<p class='empty'>Connect and sync an account to view messages.</p>");
      }

      StringBuilder conflicts = new StringBuilder();
      using (SqliteDb db = Open()) {
        foreach (Dictionary<string, string> row in db.Query(
          "SELECT * FROM connected_outbox WHERE status='conflict' ORDER BY id DESC")) {
          conflicts.Append(
            "<li><strong>" + H(Value(row, "item_kind")) + "</strong> - " +
            H(Value(row, "error_code")) + "</li>");
        }
      }
      string controls;
      if (!connected && userCode == "") {
        controls =
          "<button class='button' type='button' data-connected-action='device_start'>" +
          "Connect hosted account</button>";
      } else if (userCode != "") {
        controls =
          "<p>Enter <strong>" + H(userCode) +
          "</strong> in the hosted browser page.</p><div class='actions'>" +
          "<a class='button' href='" + H(Convert.ToString(
            status["verification_url"],
            CultureInfo.InvariantCulture)) +
          "'>Continue in browser</a><button class='button ghost' type='button' " +
          "data-connected-action='device_poll'>Check approval</button></div>";
      } else {
        controls =
          "<div class='actions'><button class='button ghost' type='button' " +
          "data-connected-action='sync'>Sync now</button><button class='button ghost' " +
          "type='button' data-connected-action='disconnect'>Disconnect</button></div>";
      }
      string composer = selectedConversation == ""
        ? ""
        : "<form class='connected-composer' data-connected-composer>" +
          "<input type='hidden' name='conversation_id' value='" +
          H(selectedConversation) + "'><textarea name='body' rows='3' " +
          "placeholder='Write a message'></textarea><label>Files (up to four)" +
          "<input name='files' type='file' multiple></label>" +
          "<button class='button' type='submit'>Queue message</button></form>";

      return
        "<section class='manage-head'><p class='kicker'>Connected account</p>" +
        "<h1>Messages</h1><p>Hosted authentication stays in your browser. " +
        "Cached history and the offline outbox are encrypted for this Windows user.</p>" +
        "</section><section class='connected-status panel'><div><h2>" +
        H(state.Replace('_', ' ')) + "</h2><p data-connected-feedback>" +
        H(Convert.ToString(status["last_error"], CultureInfo.InvariantCulture)) +
        "</p></div><div>" + controls + "</div></section>" +
        (conflicts.Length == 0
          ? ""
          : "<section class='error'><strong>Reconnect conflicts</strong><ul>" +
            conflicts + "</ul></section>") +
        "<section class='connected-layout'><aside class='panel'>" +
        conversations + "</aside><div class='panel connected-thread'><div>" +
        messages + "</div>" + composer + "</div></section>";
    }

    internal string Script(string csrfToken) {
      return
        "document.addEventListener('click',async function(e){" +
        "var b=e.target.closest&&e.target.closest('[data-connected-action]');if(!b)return;" +
        "b.disabled=true;var f=document.querySelector('[data-connected-feedback]');" +
        "try{var r=await fetch('/connected-messaging-api',{method:'POST'," +
        "headers:{'Content-Type':'application/json','X-Racinage-CSRF':'" +
        Js(csrfToken) + "'},body:JSON.stringify({action:b.dataset.connectedAction})});" +
        "var j=await r.json();if(!r.ok||!j.ok)throw new Error(j.message||'Action failed.');" +
        "if(f)f.textContent=j.message||'';if(j.verification_url)" +
        "window.location.href=j.verification_url;setTimeout(function(){location.reload();},500);" +
        "}catch(x){if(f)f.textContent=x.message;}finally{b.disabled=false;}});" +
        "document.addEventListener('submit',async function(e){" +
        "var form=e.target.closest&&e.target.closest('[data-connected-composer]');" +
        "if(!form)return;e.preventDefault();var button=form.querySelector('button');" +
        "var feedback=document.querySelector('[data-connected-feedback]');button.disabled=true;" +
        "try{var ids=[],files=form.elements.files.files;if(files.length>4)" +
        "throw new Error('A message can include four files.');" +
        "for(var i=0;i<files.length;i++){var file=files[i],u=await fetch(" +
        "'/connected-messaging-upload?conversation_id='+encodeURIComponent(" +
        "form.elements.conversation_id.value),{method:'POST',headers:{" +
        "'X-Racinage-CSRF':'" + Js(csrfToken) + "','X-File-Name':encodeURIComponent(file.name)," +
        "'Content-Type':file.type||'application/octet-stream'},body:file}),uj=await u.json();" +
        "if(!u.ok||!uj.ok)throw new Error(uj.message||'File could not be queued.');" +
        "ids.push(uj.file_queue_id);}var r=await fetch('/connected-messaging-api',{" +
        "method:'POST',headers:{'Content-Type':'application/json','X-Racinage-CSRF':'" +
        Js(csrfToken) + "'},body:JSON.stringify({action:'queue_message'," +
        "conversation_id:form.elements.conversation_id.value,body:form.elements.body.value," +
        "file_queue_ids:ids})}),j=await r.json();if(!r.ok||!j.ok)" +
        "throw new Error(j.message||'Message could not be queued.');form.reset();" +
        "if(feedback)feedback.textContent=j.message;setTimeout(function(){location.reload();},500);" +
        "}catch(x){if(feedback)feedback.textContent=x.message;}finally{button.disabled=false;}});";
    }

    private string AttachmentSummary(Dictionary<string, string> row) {
      Dictionary<string, object> metadata = SafeParse(
        SafeDecrypt(Value(row, "metadata_cipher"), "{}"));
      ArrayList attachments = GetList(metadata, "attachments");
      if (attachments.Count == 0) return "";
      StringBuilder result = new StringBuilder("<ul class='connected-attachments'>");
      foreach (object value in attachments) {
        Dictionary<string, object> attachment = value as Dictionary<string, object>;
        if (attachment == null) continue;
        result.Append(
          "<li>" + H(GetString(attachment, "name")) + " - " +
          H(GetString(attachment, "state")) + "</li>");
      }
      result.Append("</ul>");
      return result.ToString();
    }

    private void RequireConversation(string longId) {
      if (longId == "") throw new InvalidDataException("Choose a conversation.");
      using (SqliteDb db = Open()) {
        if (db.QueryOne(
          "SELECT long_id FROM connected_conversations WHERE long_id=? LIMIT 1",
          longId) == null) {
          throw new InvalidDataException("The cached conversation is unavailable.");
        }
      }
    }

    private string EnsureAccessToken() {
      Dictionary<string, string> state;
      using (SqliteDb db = Open()) {
        state = db.QueryOne("SELECT * FROM connected_account WHERE id=1");
      }
      string access = ReadProtectedToken("connected-access.token");
      if (
        access != ""
        && ParseUtc(Value(state, "access_expires_utc")) >
          DateTime.UtcNow.AddSeconds(30)
      ) {
        return access;
      }
      string refresh = ReadProtectedToken("connected-refresh.token");
      if (refresh == "") throw new InvalidOperationException("Reconnect the hosted account.");
      ApiResult result = Call(
        "POST",
        "/token/refresh",
        new Dictionary<string, object> { { "refresh_token", refresh } },
        "",
        "");
      if (result.Status != 200 && result.Status != 201) {
        DeleteToken("connected-access.token");
        DeleteToken("connected-refresh.token");
        throw ApiException(result);
      }
      SaveTokenPair(Parse(result.Body));
      return ReadProtectedToken("connected-access.token");
    }

    private void SaveTokenPair(Dictionary<string, object> pair) {
      string access = GetString(pair, "access_token");
      string refresh = GetString(pair, "refresh_token");
      if (
        !access.StartsWith("rma_", StringComparison.Ordinal)
        || !refresh.StartsWith("rmr_", StringComparison.Ordinal)
      ) {
        throw new InvalidDataException("The hosted token response was rejected.");
      }
      WriteProtectedToken("connected-access.token", access);
      WriteProtectedToken("connected-refresh.token", refresh);
      using (SqliteDb db = Open()) {
        db.Execute(
          "UPDATE connected_account SET access_expires_utc=?,refresh_expires_utc=?," +
          "state='connected',last_error='',updated_at=? WHERE id=1",
          GetString(pair, "access_expires_at_utc"),
          GetString(pair, "refresh_expires_at_utc"),
          Now());
      }
    }

    private void ExpireAccessToken() {
      DeleteToken("connected-access.token");
      using (SqliteDb db = Open()) {
        db.Execute(
          "UPDATE connected_account SET access_expires_utc='',updated_at=? WHERE id=1",
          Now());
      }
    }

    private ApiResult AuthorizedCall(
      string method,
      string path,
      Dictionary<string, object> body,
      string idempotencyKey,
      string access
    ) {
      ApiResult result = Call(method, path, body, access, idempotencyKey);
      if (result.Status != 401) return result;
      ExpireAccessToken();
      return Call(
        method,
        path,
        body,
        EnsureAccessToken(),
        idempotencyKey);
    }

    private ApiResult AuthorizedBinaryCall(
      string method,
      string path,
      byte[] bytes,
      int count,
      Dictionary<string, string> headers,
      string access
    ) {
      ApiResult result = CallBinary(method, path, bytes, count, headers, access);
      if (result.Status != 401) return result;
      ExpireAccessToken();
      return CallBinary(
        method,
        path,
        bytes,
        count,
        headers,
        EnsureAccessToken());
    }

    private ApiResult Call(
      string method,
      string path,
      Dictionary<string, object> body,
      string access,
      string idempotencyKey
    ) {
      HttpWebRequest request = CreateRequest(method, path);
      request.Accept = "application/json";
      if (access != "") {
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + access;
      }
      if (idempotencyKey != "") request.Headers["Idempotency-Key"] = idempotencyKey;
      if (body != null && method != "GET") {
        byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(body));
        request.ContentType = "application/json; charset=utf-8";
        request.ContentLength = bytes.Length;
        using (Stream output = request.GetRequestStream()) {
          output.Write(bytes, 0, bytes.Length);
        }
      }
      return ReadResponse(request);
    }

    private ApiResult CallBinary(
      string method,
      string path,
      byte[] bytes,
      int count,
      Dictionary<string, string> headers,
      string access
    ) {
      HttpWebRequest request = CreateRequest(method, path);
      request.ContentType = "application/octet-stream";
      request.Accept = "application/json";
      request.ContentLength = count;
      request.Headers[HttpRequestHeader.Authorization] = "Bearer " + access;
      foreach (KeyValuePair<string, string> header in headers) {
        request.Headers[header.Key] = header.Value;
      }
      using (Stream output = request.GetRequestStream()) output.Write(bytes, 0, count);
      return ReadResponse(request);
    }

    private static HttpWebRequest CreateRequest(string method, string path) {
      ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
      Uri uri = new Uri(ApiRoot + path, UriKind.Absolute);
      if (
        uri.Scheme != Uri.UriSchemeHttps
        || !uri.Host.Equals("racinage.com", StringComparison.OrdinalIgnoreCase)
      ) {
        throw new InvalidOperationException("The connected API address was rejected.");
      }
      HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
      request.Method = method;
      request.AllowAutoRedirect = false;
      request.AutomaticDecompression =
        DecompressionMethods.GZip | DecompressionMethods.Deflate;
      request.UserAgent = "RacinageFreeConnected/" + PortablePaths.Version;
      request.Timeout = 30000;
      request.ReadWriteTimeout = 30000;
      return request;
    }

    private static ApiResult ReadResponse(HttpWebRequest request) {
      try {
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) {
          using (StreamReader reader = new StreamReader(
            response.GetResponseStream(),
            Encoding.UTF8)) {
            return new ApiResult {
              Status = (int)response.StatusCode,
              Body = reader.ReadToEnd()
            };
          }
        }
      } catch (WebException error) {
        HttpWebResponse response = error.Response as HttpWebResponse;
        if (response == null) throw;
        using (response) {
          using (StreamReader reader = new StreamReader(
            response.GetResponseStream(),
            Encoding.UTF8)) {
            return new ApiResult {
              Status = (int)response.StatusCode,
              Body = reader.ReadToEnd()
            };
          }
        }
      }
    }

    private static ApiStreamResult OpenStream(HttpWebRequest request) {
      try {
        HttpWebResponse response = (HttpWebResponse)request.GetResponse();
        return new ApiStreamResult(response);
      } catch (WebException error) {
        HttpWebResponse response = error.Response as HttpWebResponse;
        if (response == null) throw;
        return new ApiStreamResult(response);
      }
    }

    private void MarkConflict(
      SqliteDb db,
      Dictionary<string, string> row,
      string errorCode
    ) {
      errorCode = (errorCode ?? "rejected").Trim();
      if (errorCode.Length > 120) errorCode = errorCode.Substring(0, 120);
      db.Execute(
        "UPDATE connected_outbox SET status='conflict',error_code=?,updated_at_utc=? " +
        "WHERE id=?",
        errorCode,
        Now(),
        ToLong(Value(row, "id")));
    }

    private void SetError(string message) {
      if (message == null) message = "Connected messaging failed.";
      if (message.Length > 500) message = message.Substring(0, 500);
      using (SqliteDb db = Open()) {
        db.Execute(
          "UPDATE connected_account SET last_error=?,updated_at=? WHERE id=1",
          message,
          Now());
      }
    }

    private void ClearDeviceAuthorization(string error) {
      using (SqliteDb db = Open()) {
        db.Execute(
          "UPDATE connected_account SET state='disconnected',device_code_cipher=''," +
          "user_code='',verification_url='',device_expires_utc='',last_error=?," +
          "updated_at=? WHERE id=1",
          error,
          Now());
      }
    }

    private bool HasRefreshToken() {
      return ReadProtectedToken("connected-refresh.token") != "";
    }

    private string ReadProtectedToken(string fileName) {
      string path = Path.Combine(PortablePaths.TokensDir, fileName);
      try {
        if (!File.Exists(path)) return "";
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(
          File.ReadAllBytes(path),
          TokenEntropy,
          DataProtectionScope.CurrentUser));
      } catch {
        return "";
      }
    }

    private void WriteProtectedToken(string fileName, string value) {
      File.WriteAllBytes(
        Path.Combine(PortablePaths.TokensDir, fileName),
        ProtectedData.Protect(
          Encoding.UTF8.GetBytes(value),
          TokenEntropy,
          DataProtectionScope.CurrentUser));
    }

    private static void DeleteToken(string fileName) {
      try { File.Delete(Path.Combine(PortablePaths.TokensDir, fileName)); } catch { }
    }

    private SqliteDb Open() {
      return new SqliteDb(store.DatabasePath);
    }

    private Dictionary<string, object> Parse(string value) {
      try {
        return json.Deserialize<Dictionary<string, object> >(value)
          ?? new Dictionary<string, object>();
      } catch {
        return new Dictionary<string, object>();
      }
    }

    private Dictionary<string, object> SafeParse(string value) {
      return Parse(value);
    }

    private string SafeDecrypt(string value, string fallback) {
      try {
        return value == "" ? fallback : vault.DecryptString(value);
      } catch {
        return fallback;
      }
    }

    private static Exception ApiException(ApiResult result) {
      return new InvalidOperationException(
        "Connected API rejected the request: " + ApiError(result));
    }

    private static string ApiError(ApiResult result) {
      try {
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        Dictionary<string, object> response =
          serializer.Deserialize<Dictionary<string, object> >(result.Body);
        object error;
        if (response != null && response.TryGetValue("error", out error)) {
          return Convert.ToString(error, CultureInfo.InvariantCulture);
        }
      } catch {
      }
      return "http_" + result.Status.ToString(CultureInfo.InvariantCulture);
    }

    private static ArrayList GetList(Dictionary<string, object> values, string key) {
      object value;
      if (values == null || !values.TryGetValue(key, out value) || value == null) {
        return new ArrayList();
      }
      ArrayList list = value as ArrayList;
      if (list != null) return list;
      object[] array = value as object[];
      return array == null ? new ArrayList() : new ArrayList(array);
    }

    private static List<string> GetStrings(
      Dictionary<string, object> values,
      string key
    ) {
      return GetList(values, key).Cast<object>()
        .Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))
        .Where(value => value != "")
        .ToList();
    }

    private static Dictionary<string, object> GetObject(
      Dictionary<string, object> values,
      string key
    ) {
      object value;
      return values != null
        && values.TryGetValue(key, out value)
        && value is Dictionary<string, object>
          ? (Dictionary<string, object>)value
          : new Dictionary<string, object>();
    }

    private static object GetValue(Dictionary<string, object> values, string key) {
      object value;
      return values != null && values.TryGetValue(key, out value) ? value : null;
    }

    private static string GetString(Dictionary<string, object> values, string key) {
      return Convert.ToString(GetValue(values, key), CultureInfo.InvariantCulture) ?? "";
    }

    private static int GetInt(Dictionary<string, object> values, string key) {
      return (int)Math.Min(Int32.MaxValue, GetLong(values, key));
    }

    private static long GetLong(Dictionary<string, object> values, string key) {
      object value = GetValue(values, key);
      if (value == null) return 0;
      long parsed;
      return Int64.TryParse(
        Convert.ToString(value, CultureInfo.InvariantCulture),
        NumberStyles.Any,
        CultureInfo.InvariantCulture,
        out parsed) ? parsed : 0;
    }

    private static bool GetBool(Dictionary<string, object> values, string key) {
      object value = GetValue(values, key);
      if (value is bool) return (bool)value;
      string text = Convert.ToString(value, CultureInfo.InvariantCulture);
      return text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string Value(Dictionary<string, string> values, string key) {
      string value;
      return values != null && values.TryGetValue(key, out value) ? value ?? "" : "";
    }

    private static long ToLong(object value) {
      long parsed;
      return Int64.TryParse(
        Convert.ToString(value, CultureInfo.InvariantCulture),
        NumberStyles.Any,
        CultureInfo.InvariantCulture,
        out parsed) ? parsed : 0;
    }

    private static DateTime ParseUtc(string value) {
      DateTime parsed;
      return DateTime.TryParse(
        value,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
        out parsed) ? parsed : DateTime.MinValue;
    }

    private static string DisplayUtc(string value) {
      DateTime parsed = ParseUtc(value);
      return parsed == DateTime.MinValue
        ? ""
        : parsed.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
    }

    private static void Skip(Stream stream, long bytes) {
      byte[] buffer = new byte[65536];
      while (bytes > 0) {
        int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, bytes));
        if (read < 1) throw new EndOfStreamException("The encrypted queue file is incomplete.");
        bytes -= read;
      }
    }

    private static int ReadFull(Stream stream, byte[] buffer, int count) {
      int total = 0;
      while (total < count) {
        int read = stream.Read(buffer, total, count - total);
        if (read < 1) break;
        total += read;
      }
      return total;
    }

    private static string Now() {
      return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
    }

    private static string RandomToken(int bytes) {
      byte[] value = new byte[bytes];
      using (RNGCryptoServiceProvider random = new RNGCryptoServiceProvider()) {
        random.GetBytes(value);
      }
      return BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
    }

    private static string H(string value) {
      if (value == null) return "";
      return value.Replace("&", "&amp;").Replace("<", "&lt;")
        .Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
    }

    private static string Js(string value) {
      JavaScriptSerializer serializer = new JavaScriptSerializer();
      string encoded = serializer.Serialize(value ?? "");
      return encoded.Substring(1, encoded.Length - 2);
    }
  }

  internal sealed class ConnectedVault {
    private static readonly byte[] KeyEntropy =
      Encoding.UTF8.GetBytes("Racinage Free connected outbox key v1");
    private readonly string keyPath =
      Path.Combine(PortablePaths.TokensDir, "connected-vault.key");
    private byte[] key;

    internal string EncryptString(string value) {
      using (MemoryStream input = new MemoryStream(Encoding.UTF8.GetBytes(value ?? ""))) {
        using (MemoryStream output = new MemoryStream()) {
          Encrypt(input, output, null);
          return Convert.ToBase64String(output.ToArray());
        }
      }
    }

    internal string DecryptString(string value) {
      using (MemoryStream input = new MemoryStream(Convert.FromBase64String(value))) {
        using (Stream clear = OpenDecrypted(input, true)) {
          using (StreamReader reader = new StreamReader(clear, Encoding.UTF8)) {
            return reader.ReadToEnd();
          }
        }
      }
    }

    internal ConnectedFileInfo EncryptFile(
      Stream input,
      long expectedBytes,
      string destination
    ) {
      Directory.CreateDirectory(Path.GetDirectoryName(destination));
      using (FileStream output = new FileStream(
        destination,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None)) {
        using (SHA256 sha = SHA256.Create()) {
          long size = Encrypt(input, output, sha);
          if (size != expectedBytes) {
            throw new EndOfStreamException("The queued file length changed.");
          }
          return new ConnectedFileInfo {
            Size = size,
            Sha256 = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant()
          };
        }
      }
    }

    internal Stream OpenDecryptedFile(string path) {
      return OpenDecrypted(
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
        true);
    }

    private long Encrypt(Stream input, Stream output, HashAlgorithm plaintextHash) {
      byte[] master = GetKey();
      byte[] encryptionKey = Derive(master, "encryption");
      byte[] macKey = Derive(master, "authentication");
      byte[] magic = Encoding.ASCII.GetBytes("RMSG1");
      byte[] iv = new byte[16];
      using (RNGCryptoServiceProvider random = new RNGCryptoServiceProvider()) {
        random.GetBytes(iv);
      }
      output.Write(magic, 0, magic.Length);
      output.Write(iv, 0, iv.Length);
      using (HMACSHA256 hmac = new HMACSHA256(macKey)) {
        hmac.TransformBlock(magic, 0, magic.Length, null, 0);
        hmac.TransformBlock(iv, 0, iv.Length, null, 0);
        using (MacWriteStream mac = new MacWriteStream(output, hmac)) {
          using (AesManaged aes = new AesManaged()) {
            aes.KeySize = 256;
            aes.Key = encryptionKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using (CryptoStream crypto = new CryptoStream(
              mac,
              aes.CreateEncryptor(),
              CryptoStreamMode.Write,
              true)) {
              byte[] buffer = new byte[1024 * 1024];
              long total = 0;
              while (true) {
                int read = input.Read(buffer, 0, buffer.Length);
                if (read < 1) break;
                if (plaintextHash != null) {
                  plaintextHash.TransformBlock(buffer, 0, read, null, 0);
                }
                crypto.Write(buffer, 0, read);
                total += read;
              }
              crypto.FlushFinalBlock();
              if (plaintextHash != null) {
                plaintextHash.TransformFinalBlock(new byte[0], 0, 0);
              }
              hmac.TransformFinalBlock(new byte[0], 0, 0);
              output.Write(hmac.Hash, 0, hmac.Hash.Length);
              Array.Clear(buffer, 0, buffer.Length);
              Array.Clear(encryptionKey, 0, encryptionKey.Length);
              Array.Clear(macKey, 0, macKey.Length);
              return total;
            }
          }
        }
      }
    }

    private Stream OpenDecrypted(Stream encrypted, bool ownsInput) {
      try {
        if (!encrypted.CanSeek || encrypted.Length < 5 + 16 + 32 + 16) {
          throw new InvalidDataException("The encrypted connected data is invalid.");
        }
        long authenticatedLength = encrypted.Length - 32;
        byte[] storedMac = new byte[32];
        encrypted.Position = authenticatedLength;
        ReadExact(encrypted, storedMac, storedMac.Length);
        encrypted.Position = 0;
        byte[] master = GetKey();
        byte[] macKey = Derive(master, "authentication");
        byte[] calculated;
        using (HMACSHA256 hmac = new HMACSHA256(macKey)) {
          byte[] buffer = new byte[1024 * 1024];
          long remaining = authenticatedLength;
          while (remaining > 0) {
            int read = encrypted.Read(
              buffer,
              0,
              (int)Math.Min(buffer.Length, remaining));
            if (read < 1) throw new EndOfStreamException();
            hmac.TransformBlock(buffer, 0, read, null, 0);
            remaining -= read;
          }
          hmac.TransformFinalBlock(new byte[0], 0, 0);
          calculated = hmac.Hash;
        }
        if (!FixedEquals(storedMac, calculated)) {
          throw new CryptographicException("The encrypted connected data was modified.");
        }
        encrypted.Position = 0;
        byte[] magic = new byte[5];
        ReadExact(encrypted, magic, magic.Length);
        if (Encoding.ASCII.GetString(magic) != "RMSG1") {
          throw new InvalidDataException("The encrypted connected data version is unavailable.");
        }
        byte[] iv = new byte[16];
        ReadExact(encrypted, iv, iv.Length);
        byte[] encryptionKey = Derive(master, "encryption");
        AesManaged aes = new AesManaged();
        aes.KeySize = 256;
        aes.Key = encryptionKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        LimitedReadStream limited = new LimitedReadStream(
          encrypted,
          authenticatedLength - encrypted.Position,
          ownsInput);
        return new OwnedCryptoStream(
          limited,
          aes.CreateDecryptor(),
          aes,
          encryptionKey);
      } catch {
        if (ownsInput) encrypted.Dispose();
        throw;
      }
    }

    private byte[] GetKey() {
      if (key != null) return key;
      if (File.Exists(keyPath)) {
        key = ProtectedData.Unprotect(
          File.ReadAllBytes(keyPath),
          KeyEntropy,
          DataProtectionScope.CurrentUser);
      } else {
        key = new byte[32];
        using (RNGCryptoServiceProvider random = new RNGCryptoServiceProvider()) {
          random.GetBytes(key);
        }
        File.WriteAllBytes(
          keyPath,
          ProtectedData.Protect(
            key,
            KeyEntropy,
            DataProtectionScope.CurrentUser));
      }
      if (key.Length != 32) throw new CryptographicException("The connected vault key is invalid.");
      return key;
    }

    private static byte[] Derive(byte[] master, string scope) {
      using (HMACSHA256 hmac = new HMACSHA256(master)) {
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(
          "Racinage connected messaging v1|" + scope));
      }
    }

    private static void ReadExact(Stream stream, byte[] buffer, int count) {
      int total = 0;
      while (total < count) {
        int read = stream.Read(buffer, total, count - total);
        if (read < 1) throw new EndOfStreamException();
        total += read;
      }
    }

    private static bool FixedEquals(byte[] a, byte[] b) {
      if (a == null || b == null || a.Length != b.Length) return false;
      int difference = 0;
      for (int index = 0; index < a.Length; index++) {
        difference |= a[index] ^ b[index];
      }
      return difference == 0;
    }
  }

  internal sealed class MacWriteStream : Stream {
    private readonly Stream output;
    private readonly HMAC hmac;

    internal MacWriteStream(Stream output, HMAC hmac) {
      this.output = output;
      this.hmac = hmac;
    }

    public override void Write(byte[] buffer, int offset, int count) {
      hmac.TransformBlock(buffer, offset, count, null, 0);
      output.Write(buffer, offset, count);
    }

    public override void Flush() { output.Flush(); }
    public override bool CanRead { get { return false; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return true; } }
    public override long Length { get { return output.Length; } }
    public override long Position {
      get { return output.Position; }
      set { throw new NotSupportedException(); }
    }
    public override int Read(byte[] buffer, int offset, int count) {
      throw new NotSupportedException();
    }
    public override long Seek(long offset, SeekOrigin origin) {
      throw new NotSupportedException();
    }
    public override void SetLength(long value) { throw new NotSupportedException(); }
  }

  internal sealed class LimitedReadStream : Stream {
    private readonly Stream input;
    private readonly bool ownsInput;
    private long remaining;

    internal LimitedReadStream(Stream input, long length, bool ownsInput) {
      this.input = input;
      this.remaining = length;
      this.ownsInput = ownsInput;
    }

    public override int Read(byte[] buffer, int offset, int count) {
      if (remaining <= 0) return 0;
      int read = input.Read(buffer, offset, (int)Math.Min(count, remaining));
      remaining -= read;
      return read;
    }

    protected override void Dispose(bool disposing) {
      if (disposing && ownsInput) input.Dispose();
      base.Dispose(disposing);
    }

    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position {
      get { throw new NotSupportedException(); }
      set { throw new NotSupportedException(); }
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) {
      throw new NotSupportedException();
    }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) {
      throw new NotSupportedException();
    }
  }

  internal sealed class OwnedCryptoStream : CryptoStream {
    private readonly IDisposable algorithm;
    private readonly byte[] key;

    internal OwnedCryptoStream(
      Stream input,
      ICryptoTransform transform,
      IDisposable algorithm,
      byte[] key
    ) : base(input, transform, CryptoStreamMode.Read) {
      this.algorithm = algorithm;
      this.key = key;
    }

    protected override void Dispose(bool disposing) {
      base.Dispose(disposing);
      if (disposing) {
        algorithm.Dispose();
        Array.Clear(key, 0, key.Length);
      }
    }
  }

  internal sealed class ConnectedFileInfo {
    internal long Size;
    internal string Sha256;
  }

  internal sealed class ApiResult {
    internal int Status;
    internal string Body;
  }

  internal sealed class ApiStreamResult : IDisposable {
    private readonly HttpWebResponse response;
    internal int Status { get; private set; }
    internal Stream Stream { get; private set; }

    internal ApiStreamResult(HttpWebResponse response) {
      this.response = response;
      Status = (int)response.StatusCode;
      Stream = response.GetResponseStream();
    }

    public void Dispose() {
      if (Stream != null) Stream.Dispose();
      response.Dispose();
    }
  }
}
