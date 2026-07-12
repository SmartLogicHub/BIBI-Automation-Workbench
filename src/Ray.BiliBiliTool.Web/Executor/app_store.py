import sqlite3
from contextlib import contextmanager
from datetime import datetime, timedelta
from pathlib import Path
import random
import re

from openpyxl import Workbook

from url_utils import canonical_bili_url, extract_bvid

DEFAULT_DEEPSEEK_MODEL = "deepseek-v4-flash"
DEFAULT_QWEN_MODEL = "qwen3.6-plus"
DEPRECATED_DEEPSEEK_MODELS = {"deepseek-chat", "deepseek-reasoner"}


def now_iso():
    return datetime.now().astimezone().isoformat(timespec="seconds")


def parse_iso_datetime(value):
    try:
        return datetime.fromisoformat(str(value or ""))
    except Exception:
        return None


def seconds_until(value):
    target = parse_iso_datetime(value)
    if not target:
        return 0
    return max(0, int((target - datetime.now().astimezone()).total_seconds()))


def mask_secret(value):
    text = str(value or "")
    if not text:
        return ""
    if len(text) <= 8:
        return "*" * len(text)
    return f"{text[:3]}...{text[-4:]}"


def normalize_deepseek_model(value):
    model = str(value or "").strip()
    if not model or model in DEPRECATED_DEEPSEEK_MODELS:
        return DEFAULT_DEEPSEEK_MODEL
    return model


def account_slug(name):
    slug = re.sub(r'[\\/:*?"<>|\s]+', "_", str(name or "").strip())
    slug = slug.strip("._")
    return slug or "account"


class AppStore:
    def __init__(self, db_path):
        self.db_path = Path(db_path)

    @contextmanager
    def connect(self):
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        connection = sqlite3.connect(self.db_path)
        connection.row_factory = sqlite3.Row
        try:
            yield connection
            connection.commit()
        finally:
            connection.close()

    def initialize(self):
        with self.connect() as db:
            db.executescript(
                """
                CREATE TABLE IF NOT EXISTS accounts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL UNIQUE,
                    user_data_dir TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    daily_limit INTEGER NOT NULL DEFAULT 0,
                    today_count INTEGER NOT NULL DEFAULT 0,
                    last_used_at TEXT NOT NULL DEFAULT '',
                    next_available_at TEXT NOT NULL DEFAULT '',
                    status TEXT NOT NULL DEFAULT 'idle',
                    note TEXT NOT NULL DEFAULT '',
                    deleted INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS templates (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    text TEXT NOT NULL UNIQUE,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    use_count INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS comment_libraries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL UNIQUE,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS comment_library_templates (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    library_id INTEGER NOT NULL,
                    text TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    use_count INTEGER NOT NULL DEFAULT 0,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(library_id, text)
                );

                CREATE TABLE IF NOT EXISTS videos (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    bvid TEXT NOT NULL UNIQUE,
                    url TEXT NOT NULL,
                    title TEXT NOT NULL DEFAULT '',
                    source TEXT NOT NULL DEFAULT 'manual',
                    source_keyword TEXT NOT NULL DEFAULT '',
                    status TEXT NOT NULL DEFAULT 'pending',
                    last_error TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ledger (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    video_id INTEGER,
                    bvid TEXT NOT NULL,
                    url TEXT NOT NULL,
                    account_id INTEGER,
                    account_name TEXT NOT NULL,
                    template_text TEXT NOT NULL,
                    status TEXT NOT NULL,
                    error_reason TEXT NOT NULL DEFAULT '',
                    platform_comment_id TEXT NOT NULL DEFAULT '',
                    verification_method TEXT NOT NULL DEFAULT '',
                    proof_text TEXT NOT NULL DEFAULT '',
                    comment_url TEXT NOT NULL DEFAULT '',
                    verified_at TEXT NOT NULL DEFAULT '',
                    published_at TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS run_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    job_id TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    message TEXT NOT NULL,
                    payload TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL DEFAULT '',
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS daily_stats (
                    stat_date TEXT NOT NULL,
                    key TEXT NOT NULL,
                    value INTEGER NOT NULL DEFAULT 0,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY (stat_date, key)
                );

                CREATE TABLE IF NOT EXISTS deleted_account_names (
                    name TEXT PRIMARY KEY,
                    deleted_at TEXT NOT NULL
                );
                """
            )
            self._ensure_column(db, "accounts", "note", "TEXT NOT NULL DEFAULT ''")
            self._ensure_column(db, "accounts", "deleted", "INTEGER NOT NULL DEFAULT 0")
            self._ensure_column(db, "accounts", "login_status", "TEXT NOT NULL DEFAULT 'unknown'")
            self._ensure_column(db, "accounts", "last_login_checked_at", "TEXT NOT NULL DEFAULT ''")
            self._ensure_column(db, "accounts", "login_note", "TEXT NOT NULL DEFAULT ''")
            self._ensure_column(db, "accounts", "cookie", "TEXT NOT NULL DEFAULT ''")
            self._ensure_column(db, "accounts", "proxy", "TEXT NOT NULL DEFAULT ''")
            self._ensure_column(db, "accounts", "uid", "TEXT NOT NULL DEFAULT ''")
            self._ensure_column(db, "accounts", "nickname", "TEXT NOT NULL DEFAULT ''")
            self._ensure_column(db, "accounts", "avatar_url", "TEXT NOT NULL DEFAULT ''")
            self._ensure_column(db, "accounts", "today_count_date", "TEXT NOT NULL DEFAULT ''")
            self._ensure_column(db, "comment_library_templates", "sort_order", "INTEGER NOT NULL DEFAULT 0")
            self._ensure_column(db, "ledger", "platform_comment_id", "TEXT NOT NULL DEFAULT ''")
            self._ensure_column(db, "ledger", "verification_method", "TEXT NOT NULL DEFAULT ''")
            self._ensure_column(db, "ledger", "proof_text", "TEXT NOT NULL DEFAULT ''")
            self._ensure_column(db, "ledger", "comment_url", "TEXT NOT NULL DEFAULT ''")
            self._ensure_column(db, "ledger", "verified_at", "TEXT NOT NULL DEFAULT ''")
            db.execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_uid_unique ON accounts(uid) WHERE uid<>''")
            db.execute(
                """
                INSERT OR IGNORE INTO deleted_account_names (name, deleted_at)
                SELECT name, updated_at FROM accounts WHERE deleted=1
                """
            )
            db.execute("DELETE FROM accounts WHERE deleted=1")

    def rebase_account_directories(self, base_dir):
        """Keep browser profiles portable when the application directory changes."""
        base = Path(base_dir or self.db_path.parent).resolve()
        accounts_root = (base / "accounts").resolve()
        accounts_root.mkdir(parents=True, exist_ok=True)
        timestamp = now_iso()
        with self.connect() as db:
            rows = db.execute(
                "SELECT id, name, user_data_dir FROM accounts WHERE deleted=0 ORDER BY id ASC"
            ).fetchall()
            used_paths = set()
            for row in rows:
                profile_name = account_slug(row["name"])
                expected = (accounts_root / profile_name).resolve()
                normalized = str(expected).casefold()
                if normalized in used_paths:
                    expected = (accounts_root / f"{profile_name}-{int(row['id'])}").resolve()
                    normalized = str(expected).casefold()
                used_paths.add(normalized)
                current = Path(str(row["user_data_dir"] or ""))
                try:
                    current = current.resolve()
                except OSError:
                    pass
                if current == expected:
                    continue
                db.execute(
                    "UPDATE accounts SET user_data_dir=?, updated_at=? WHERE id=?",
                    (str(expected), timestamp, int(row["id"])),
                )

    def sync_accounts_from_config(self, config, base_dir=None):
        base = Path(base_dir or ".").resolve()
        timestamp = now_iso()
        with self.connect() as db:
            deleted_names = {
                row["name"]
                for row in db.execute("SELECT name FROM deleted_account_names").fetchall()
            }
            for item in config.get("accounts", []) or []:
                name = str(item.get("name") or "").strip()
                if not name or name in deleted_names:
                    continue
                raw_dir = Path(str(item.get("user_data_dir") or f"accounts/{name}"))
                user_data_dir = raw_dir if raw_dir.is_absolute() else base / raw_dir
                db.execute(
                    """
                    INSERT INTO accounts (name, user_data_dir, enabled, daily_limit, note, deleted, created_at, updated_at)
                    VALUES (?, ?, ?, ?, '', 0, ?, ?)
                    ON CONFLICT(name) DO UPDATE SET
                        user_data_dir=excluded.user_data_dir,
                        enabled=excluded.enabled,
                        daily_limit=excluded.daily_limit,
                        updated_at=excluded.updated_at
                    WHERE accounts.deleted=0
                    """,
                    (
                        name,
                        str(user_data_dir),
                        1 if item.get("enabled", True) else 0,
                        int(item.get("daily_limit") or 0),
                        timestamp,
                        timestamp,
                    ),
                )

    def create_account(self, payload, base_dir=None):
        name = str((payload or {}).get("name") or "").strip()
        if not name:
            raise ValueError("账号名称不能为空")
        daily_limit = int((payload or {}).get("dailyLimit", (payload or {}).get("daily_limit", 20)) or 0)
        enabled = 1 if (payload or {}).get("enabled", True) else 0
        note = str((payload or {}).get("note") or "").strip()
        base = Path(base_dir or self.db_path.parent).resolve()
        raw_dir = str((payload or {}).get("userDataDir") or (payload or {}).get("user_data_dir") or "")
        user_data_dir = Path(raw_dir) if raw_dir else Path("accounts") / account_slug(name)
        if not user_data_dir.is_absolute():
            user_data_dir = base / user_data_dir
        timestamp = now_iso()
        with self.connect() as db:
            db.execute("DELETE FROM deleted_account_names WHERE name=?", (name,))
            existing = db.execute("SELECT * FROM accounts WHERE name=?", (name,)).fetchone()
            if existing:
                raise ValueError("账号名称已存在")
            else:
                try:
                    cursor = db.execute(
                        """
                        INSERT INTO accounts (
                            name, user_data_dir, enabled, daily_limit, today_count,
                            last_used_at, next_available_at, status, note, deleted,
                            created_at, updated_at
                        )
                        VALUES (?, ?, ?, ?, 0, '', '', 'idle', ?, 0, ?, ?)
                        """,
                        (name, str(user_data_dir), enabled, daily_limit, note, timestamp, timestamp),
                    )
                except sqlite3.IntegrityError as error:
                    raise ValueError("账号名称已存在") from error
                account_id = cursor.lastrowid
                duplicate_profile = db.execute(
                    "SELECT 1 FROM accounts WHERE id<>? AND lower(user_data_dir)=lower(?) LIMIT 1",
                    (account_id, str(user_data_dir)),
                ).fetchone()
                if duplicate_profile:
                    user_data_dir = user_data_dir.with_name(f"{user_data_dir.name}-{account_id}")
                    db.execute(
                        "UPDATE accounts SET user_data_dir=?, updated_at=? WHERE id=?",
                        (str(user_data_dir), timestamp, account_id),
                    )
        return self.get_account(account_id)

    def get_account(self, account_id):
        with self.connect() as db:
            self._reset_stale_daily_counts(db)
            row = db.execute("SELECT * FROM accounts WHERE id=? AND deleted=0", (int(account_id),)).fetchone()
        if not row:
            return None
        return self._account_dict(row)

    def update_account(self, account_id, payload):
        fields = []
        params = []
        payload = payload or {}
        requested_name = None
        if "name" in payload:
            name = str(payload.get("name") or "").strip()
            if not name:
                raise ValueError("账号名称不能为空")
            requested_name = name
            fields.append("name=?")
            params.append(name)
        if "enabled" in payload:
            fields.append("enabled=?")
            params.append(1 if payload.get("enabled") else 0)
        if "dailyLimit" in payload or "daily_limit" in payload:
            fields.append("daily_limit=?")
            params.append(int(payload.get("dailyLimit", payload.get("daily_limit")) or 0))
        if "note" in payload:
            fields.append("note=?")
            params.append(str(payload.get("note") or "").strip())
        if "status" in payload:
            fields.append("status=?")
            params.append(str(payload.get("status") or "").strip() or "idle")
        if "loginStatus" in payload or "login_status" in payload:
            fields.append("login_status=?")
            params.append(str(payload.get("loginStatus", payload.get("login_status")) or "").strip() or "unknown")
        if "cookie" in payload:
            fields.append("cookie=?")
            params.append(str(payload.get("cookie") or "").strip())
        if "proxy" in payload:
            fields.append("proxy=?")
            params.append(str(payload.get("proxy") or "").strip())
        if "uid" in payload:
            fields.append("uid=?")
            params.append(str(payload.get("uid") or "").strip())
        if "nickname" in payload:
            fields.append("nickname=?")
            params.append(str(payload.get("nickname") or "").strip())
        if "avatarUrl" in payload or "avatar_url" in payload:
            fields.append("avatar_url=?")
            params.append(str(payload.get("avatarUrl", payload.get("avatar_url")) or "").strip())
        if not fields:
            return self.get_account(account_id)
        fields.append("updated_at=?")
        timestamp = now_iso()
        params.append(timestamp)
        params.append(int(account_id))
        with self.connect() as db:
            if requested_name:
                conflict = db.execute(
                    "SELECT id, deleted FROM accounts WHERE name=? AND id!=?",
                    (requested_name, int(account_id)),
                ).fetchone()
                if conflict:
                    if not int(conflict["deleted"] or 0):
                        raise ValueError("账号名称已存在")
                    self._release_deleted_account_name(db, requested_name, conflict["id"], timestamp)
                db.execute("DELETE FROM deleted_account_names WHERE name=?", (requested_name,))
            try:
                cursor = db.execute(
                    f"UPDATE accounts SET {', '.join(fields)} WHERE id=? AND deleted=0",
                    params,
                )
            except sqlite3.IntegrityError as error:
                if "uid" in str(error).lower():
                    raise ValueError("该 B 站账号已经托管") from error
                raise ValueError("账号名称已存在") from error
            if cursor.rowcount <= 0:
                raise ValueError("账号不存在")
        return self.get_account(account_id)

    def delete_account(self, account_id):
        timestamp = now_iso()
        with self.connect() as db:
            row = db.execute("SELECT name FROM accounts WHERE id=? AND deleted=0", (int(account_id),)).fetchone()
            if not row:
                return False
            db.execute(
                "INSERT OR REPLACE INTO deleted_account_names (name, deleted_at) VALUES (?, ?)",
                (row["name"], timestamp),
            )
            cursor = db.execute("DELETE FROM accounts WHERE id=?", (int(account_id),))
        return cursor.rowcount > 0

    def save_ai_settings(self, payload):
        payload = payload or {}
        mapping = {
            "deepseekApiKey": "deepseek_api_key",
            "deepseekModel": "deepseek_model",
            "deepseekBaseUrl": "deepseek_base_url",
            "qwenApiKey": "qwen_api_key",
            "qwenModel": "qwen_model",
            "qwenBaseUrl": "qwen_base_url",
            "defaultSearchRules": "default_search_rules",
        }
        updates = {}
        for source, key in mapping.items():
            if source in payload:
                value = str(payload.get(source) or "").strip()
                if key == "deepseek_model":
                    value = normalize_deepseek_model(value)
                elif key == "qwen_model" and not value:
                    value = DEFAULT_QWEN_MODEL
                updates[key] = value
        deepseek = payload.get("deepseek") if isinstance(payload.get("deepseek"), dict) else {}
        if deepseek:
            if deepseek.get("clearApiKey") is True:
                updates["deepseek_api_key"] = ""
            elif "apiKey" in deepseek and str(deepseek.get("apiKey") or "").strip():
                updates["deepseek_api_key"] = str(deepseek.get("apiKey") or "").strip()
            if "model" in deepseek:
                updates["deepseek_model"] = normalize_deepseek_model(deepseek.get("model"))
            if "baseUrl" in deepseek:
                updates["deepseek_base_url"] = str(deepseek.get("baseUrl") or "").strip()
        qwen = payload.get("qwen") if isinstance(payload.get("qwen"), dict) else {}
        if qwen:
            if qwen.get("clearApiKey") is True:
                updates["qwen_api_key"] = ""
            elif "apiKey" in qwen and str(qwen.get("apiKey") or "").strip():
                updates["qwen_api_key"] = str(qwen.get("apiKey") or "").strip()
            if "model" in qwen:
                updates["qwen_model"] = str(qwen.get("model") or DEFAULT_QWEN_MODEL).strip() or DEFAULT_QWEN_MODEL
            if "baseUrl" in qwen:
                updates["qwen_base_url"] = str(qwen.get("baseUrl") or "").strip()
        if not updates:
            return self.get_settings()
        timestamp = now_iso()
        with self.connect() as db:
            for key, value in updates.items():
                db.execute(
                    """
                    INSERT INTO settings (key, value, updated_at)
                    VALUES (?, ?, ?)
                    ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at
                    """,
                    (key, value, timestamp),
                )
        return self.get_settings()

    def get_settings(self, include_secrets=False):
        values = {
            "deepseek_model": DEFAULT_DEEPSEEK_MODEL,
            "qwen_model": DEFAULT_QWEN_MODEL,
            "deepseek_api_key": "",
            "qwen_api_key": "",
            "deepseek_base_url": "",
            "qwen_base_url": "",
            "default_search_rules": "",
        }
        with self.connect() as db:
            for row in db.execute("SELECT key, value FROM settings").fetchall():
                values[row["key"]] = row["value"]
        deepseek_model = normalize_deepseek_model(values["deepseek_model"])
        qwen_model = values["qwen_model"] or DEFAULT_QWEN_MODEL
        deepseek = {
            "configured": bool(values["deepseek_api_key"]),
            "maskedKey": mask_secret(values["deepseek_api_key"]),
            "model": deepseek_model,
            "baseUrl": values["deepseek_base_url"],
        }
        qwen = {
            "configured": bool(values["qwen_api_key"]),
            "maskedKey": mask_secret(values["qwen_api_key"]),
            "model": qwen_model,
            "baseUrl": values["qwen_base_url"],
        }
        if include_secrets:
            deepseek["apiKey"] = values["deepseek_api_key"]
            qwen["apiKey"] = values["qwen_api_key"]
        return {
            "deepseek": deepseek,
            "qwen": qwen,
            "defaultSearchRules": values["default_search_rules"],
        }

    def save_comment_library(self, name, templates=None, enabled=True):
        name = str(name or "").strip()
        if not name:
            raise ValueError("产品评论库名称不能为空")
        timestamp = now_iso()
        with self.connect() as db:
            db.execute(
                """
                INSERT INTO comment_libraries (name, enabled, created_at, updated_at)
                VALUES (?, ?, ?, ?)
                ON CONFLICT(name) DO UPDATE SET
                    enabled=excluded.enabled,
                    updated_at=excluded.updated_at
                """,
                (name, 1 if enabled else 0, timestamp, timestamp),
            )
            row = db.execute("SELECT * FROM comment_libraries WHERE name=?", (name,)).fetchone()
            library_id = row["id"]
        if templates is not None:
            self.save_library_templates(library_id, templates)
        return self.get_comment_library(library_id)

    def update_comment_library(self, library_id, name, templates=None, enabled=True):
        library_id = int(library_id)
        name = str(name or "").strip()
        if not name:
            raise ValueError("产品评论库名称不能为空")
        timestamp = now_iso()
        with self.connect() as db:
            existing = db.execute(
                "SELECT id FROM comment_libraries WHERE id=?",
                (library_id,),
            ).fetchone()
            if not existing:
                raise ValueError("产品评论库不存在")
            db.execute(
                """
                UPDATE comment_libraries
                SET name=?, enabled=?, updated_at=?
                WHERE id=?
                """,
                (name, 1 if enabled else 0, timestamp, library_id),
            )
        if templates is not None:
            self.save_library_templates(library_id, templates)
        return self.get_comment_library(library_id)

    def get_comment_library(self, library_id):
        with self.connect() as db:
            row = db.execute(
                """
                SELECT l.*, COUNT(t.id) AS template_count
                FROM comment_libraries l
                LEFT JOIN comment_library_templates t
                  ON t.library_id=l.id AND t.enabled=1
                WHERE l.id=?
                GROUP BY l.id
                """,
                (int(library_id),),
            ).fetchone()
        return self._comment_library_dict(row) if row else None

    def list_comment_libraries(self, enabled_only=False):
        query = """
            SELECT l.*, COUNT(t.id) AS template_count
            FROM comment_libraries l
            LEFT JOIN comment_library_templates t
              ON t.library_id=l.id AND t.enabled=1
        """
        params = []
        if enabled_only:
            query += " WHERE l.enabled=1"
        query += " GROUP BY l.id ORDER BY l.name COLLATE NOCASE ASC, l.id ASC"
        with self.connect() as db:
            return [self._comment_library_dict(row) for row in db.execute(query, params).fetchall()]

    def list_library_templates(self, library_id, enabled_only=False):
        query = "SELECT * FROM comment_library_templates WHERE library_id=?"
        params = [int(library_id)]
        if enabled_only:
            query += " AND enabled=1"
        query += " ORDER BY sort_order ASC, id ASC"
        with self.connect() as db:
            return [dict(row) for row in db.execute(query, params).fetchall()]

    def save_library_templates(self, library_id, templates):
        cleaned = self._clean_template_lines(templates)
        timestamp = now_iso()
        with self.connect() as db:
            row = db.execute("SELECT id FROM comment_libraries WHERE id=?", (int(library_id),)).fetchone()
            if not row:
                raise ValueError("产品评论库不存在")
            db.execute(
                "UPDATE comment_library_templates SET enabled=0, updated_at=? WHERE library_id=?",
                (timestamp, int(library_id)),
            )
            for index, text in enumerate(cleaned):
                db.execute(
                    """
                    INSERT INTO comment_library_templates (
                        library_id, text, enabled, sort_order, created_at, updated_at
                    )
                    VALUES (?, ?, 1, ?, ?, ?)
                    ON CONFLICT(library_id, text) DO UPDATE SET
                        enabled=1,
                        sort_order=excluded.sort_order,
                        updated_at=excluded.updated_at
                    """,
                    (int(library_id), text, index, timestamp, timestamp),
                )
            db.execute("UPDATE comment_libraries SET updated_at=? WHERE id=?", (timestamp, int(library_id)))
        return {"count": len(cleaned)}

    def delete_comment_library(self, library_id):
        with self.connect() as db:
            db.execute("DELETE FROM comment_library_templates WHERE library_id=?", (int(library_id),))
            cursor = db.execute("DELETE FROM comment_libraries WHERE id=?", (int(library_id),))
        return {"deleted": int(cursor.rowcount or 0)}

    def import_comment_libraries_from_directory(self, directory):
        source = Path(directory)
        if not source.exists() or not source.is_dir():
            raise ValueError("评论词文件夹不存在")
        imported = []
        skipped = []
        for path in sorted(source.glob("*.txt"), key=lambda item: item.name.lower()):
            name = path.stem.strip()
            if not name:
                skipped.append({"file": str(path), "reason": "empty_name"})
                continue
            try:
                lines = path.read_text(encoding="utf-8").splitlines()
            except UnicodeDecodeError:
                lines = path.read_text(encoding="utf-8-sig").splitlines()
            library = self.save_comment_library(name, lines, enabled=True)
            imported.append(
                {
                    "name": library["name"],
                    "id": library["id"],
                    "template_count": library["template_count"],
                    "file": str(path),
                }
            )
        return {"imported": len(imported), "libraries": imported, "skipped": skipped}

    def save_templates(self, templates):
        cleaned = []
        seen = set()
        for value in templates or []:
            text = str(value or "").strip()
            if not text or text in seen:
                continue
            seen.add(text)
            cleaned.append(text)
        timestamp = now_iso()
        with self.connect() as db:
            db.execute("UPDATE templates SET enabled=0, updated_at=?", (timestamp,))
            for text in cleaned:
                db.execute(
                    """
                    INSERT INTO templates (text, enabled, created_at, updated_at)
                    VALUES (?, 1, ?, ?)
                    ON CONFLICT(text) DO UPDATE SET enabled=1, updated_at=excluded.updated_at
                    """,
                    (text, timestamp, timestamp),
                )
        return {"count": len(cleaned)}

    def import_videos(self, urls, source="manual", dry_run=False):
        existing = self.existing_bvids()
        seen = set()
        items = []
        inserted = duplicate = invalid = 0
        timestamp = now_iso()
        with self.connect() as db:
            for raw_url in urls or []:
                bvid = extract_bvid(raw_url)
                if not bvid:
                    invalid += 1
                    items.append({"input": raw_url, "status": "invalid"})
                    continue
                url = canonical_bili_url(raw_url)
                if bvid in existing or bvid in seen:
                    duplicate += 1
                    items.append({"input": raw_url, "bvid": bvid, "url": url, "status": "duplicate"})
                    continue
                seen.add(bvid)
                inserted += 1
                items.append({"input": raw_url, "bvid": bvid, "url": url, "status": "inserted"})
                if not dry_run:
                    db.execute(
                        """
                        INSERT INTO videos (bvid, url, source, status, created_at, updated_at)
                        VALUES (?, ?, ?, 'pending', ?, ?)
                        """,
                        (bvid, url, source, timestamp, timestamp),
                    )
            if not dry_run:
                self._increment_stat(db, "videos_inserted", inserted, timestamp)
                self._increment_stat(db, "duplicates_skipped", duplicate, timestamp)
                self._increment_stat(db, "invalid_skipped", invalid, timestamp)
                if source == "search":
                    self._increment_stat(db, "searched_videos", len(urls or []), timestamp)
        return {
            "total": len(urls or []),
            "inserted": inserted,
            "duplicate": duplicate,
            "invalid": invalid,
            "items": items,
            "dryRun": bool(dry_run),
        }

    def existing_bvids(self):
        with self.connect() as db:
            rows = db.execute("SELECT bvid FROM videos UNION SELECT bvid FROM ledger").fetchall()
        return {row["bvid"] for row in rows if row["bvid"]}

    def list_accounts(self, enabled_only=False):
        query = "SELECT * FROM accounts WHERE deleted=0"
        params = []
        if enabled_only:
            query += " AND enabled=1"
        query += " ORDER BY name ASC"
        with self.connect() as db:
            self._reset_stale_daily_counts(db)
            return [self._account_dict(row) for row in db.execute(query, params).fetchall()]

    def list_templates(self, enabled_only=False):
        query = "SELECT * FROM templates"
        if enabled_only:
            query += " WHERE enabled=1"
        query += " ORDER BY id ASC"
        with self.connect() as db:
            return [dict(row) for row in db.execute(query).fetchall()]

    def list_videos(self, status=None, limit=200):
        params = []
        query = "SELECT * FROM videos"
        if status:
            query += " WHERE status=?"
            params.append(status)
        query += " ORDER BY id DESC LIMIT ?"
        params.append(int(limit or 200))
        with self.connect() as db:
            return [dict(row) for row in db.execute(query, params).fetchall()]

    def clear_video_queue(self):
        with self.connect() as db:
            cursor = db.execute("DELETE FROM videos")
        return {"deleted": int(cursor.rowcount or 0)}

    def delete_video(self, video_id):
        with self.connect() as db:
            cursor = db.execute("DELETE FROM videos WHERE id=?", (int(video_id),))
        return {"deleted": int(cursor.rowcount or 0)}

    def clear_ledger(self):
        with self.connect() as db:
            cursor = db.execute("DELETE FROM ledger")
        return {"deleted": int(cursor.rowcount or 0)}

    def delete_ledger_entry(self, ledger_id):
        with self.connect() as db:
            cursor = db.execute("DELETE FROM ledger WHERE id=?", (int(ledger_id),))
        return {"deleted": int(cursor.rowcount or 0)}

    def list_ledger(self, limit=200):
        with self.connect() as db:
            rows = db.execute(
                """
                SELECT ledger.*, COALESCE(videos.title, '') AS video_title
                FROM ledger
                LEFT JOIN videos ON videos.id=ledger.video_id
                ORDER BY ledger.id DESC
                LIMIT ?
                """,
                (int(limit or 200),),
            ).fetchall()
        return [dict(row) for row in rows]

    def export_ledger_excel(self, output_file):
        path = Path(output_file)
        path.parent.mkdir(parents=True, exist_ok=True)
        rows = self.list_ledger(limit=100000)
        headers = [
            "account_name",
            "video_title",
            "bvid",
            "url",
            "template_text",
            "status",
            "verification_method",
            "proof_text",
            "platform_comment_id",
            "comment_url",
            "error_reason",
            "verified_at",
            "published_at",
            "created_at",
        ]
        workbook = Workbook()
        sheet = workbook.active
        sheet.title = "ledger"
        sheet.append(headers)
        for row in rows:
            sheet.append([row.get(header, "") for header in headers])
        workbook.save(path)
        workbook.close()
        return {"count": len(rows), "file": str(path)}

    def summary(self):
        current_iso = now_iso()
        with self.connect() as db:
            self._reset_stale_daily_counts(db, current_iso[:10])
            account_count = db.execute(
                "SELECT COUNT(*) AS count FROM accounts WHERE deleted=0 AND enabled=1"
            ).fetchone()["count"]
            library_count = db.execute("SELECT COUNT(*) AS count FROM comment_libraries WHERE enabled=1").fetchone()["count"]
            template_count = db.execute(
                """
                SELECT COUNT(*) AS count
                FROM comment_library_templates t
                JOIN comment_libraries l ON l.id=t.library_id
                WHERE t.enabled=1 AND l.enabled=1
                """
            ).fetchone()["count"]
            pending = db.execute("SELECT COUNT(*) AS count FROM videos WHERE status='pending'").fetchone()["count"]
            success = db.execute("SELECT COUNT(*) AS count FROM ledger WHERE status='success'").fetchone()["count"]
            pending_confirmation = db.execute(
                "SELECT COUNT(*) AS count FROM ledger WHERE status='submitted_unverified'"
            ).fetchone()["count"]
            failed = db.execute(
                "SELECT COUNT(*) AS count FROM ledger WHERE status NOT IN ('success', 'submitted_unverified')"
            ).fetchone()["count"]
            today = now_iso()[:10]
            stats = {
                row["key"]: row["value"]
                for row in db.execute("SELECT key, value FROM daily_stats WHERE stat_date=?", (today,)).fetchall()
            }
            next_row = db.execute(
                """
                SELECT next_available_at FROM accounts
                WHERE deleted=0 AND enabled=1
                  AND lower(login_status) IN ('ok', 'logged_in', 'success')
                  AND uid<>'' AND cookie<>''
                  AND next_available_at!='' AND next_available_at > ?
                ORDER BY next_available_at ASC LIMIT 1
                """,
                (current_iso,),
            ).fetchone()
            available_now = db.execute(
                """
                SELECT COUNT(*) AS count FROM accounts
                WHERE deleted=0 AND enabled=1
                  AND lower(login_status) IN ('ok', 'logged_in', 'success')
                  AND uid<>'' AND cookie<>''
                  AND (daily_limit=0 OR today_count < daily_limit)
                  AND (next_available_at='' OR next_available_at <= ?)
                """,
                (current_iso,),
            ).fetchone()["count"]
            account_rows = db.execute(
                """
                SELECT id, name, enabled, daily_limit, today_count, last_used_at, next_available_at
                FROM accounts
                WHERE deleted=0 AND enabled=1
                ORDER BY name ASC
                """
            ).fetchall()
        account_details = []
        cooldown_count = 0
        for row in account_rows:
            remaining = seconds_until(row["next_available_at"])
            if remaining > 0:
                cooldown_count += 1
            last_used = parse_iso_datetime(row["last_used_at"])
            next_available = parse_iso_datetime(row["next_available_at"])
            total = int((next_available - last_used).total_seconds()) if last_used and next_available and next_available > last_used else 0
            if remaining <= 0:
                cooldown_percent = 0
            elif total > 0:
                cooldown_percent = max(1, min(100, int((remaining / total) * 100)))
            else:
                cooldown_percent = 100
            account_details.append(
                {
                    "id": row["id"],
                    "name": row["name"],
                    "today_count": row["today_count"],
                    "daily_limit": row["daily_limit"],
                    "last_used_at": row["last_used_at"],
                    "next_available_at": row["next_available_at"],
                    "cooldown_remaining_seconds": remaining,
                    "cooldown_percent": cooldown_percent,
                }
            )
        return {
            "account_count": account_count,
            "available_account_count": available_now,
            "cooldown_account_count": cooldown_count,
            "accounts": account_details,
            "library_count": library_count,
            "template_count": template_count,
            "queue_pending": pending,
            "success_count": success,
            "pending_confirmation_count": pending_confirmation,
            "failed_count": failed,
            "today_search_count": stats.get("searched_videos", 0),
            "today_inserted_count": stats.get("videos_inserted", 0),
            "today_duplicate_count": stats.get("duplicates_skipped", 0),
            "next_available_at": next_row["next_available_at"] if next_row else "",
        }

    def build_comment_plan(self, max_count=0, account_ids=None, library_id=None):
        accounts = self._eligible_accounts(account_ids or [])
        is_ai = str(library_id or "").startswith("__AI__") or str(library_id or "").lower() in ["deepseek", "qwen"]
        if is_ai:
            library = {"id": library_id, "name": "AI 智能发评", "enabled": True}
            templates = [{"id": "ai_dummy", "text": "AI 正在分析并生成评论..."}]
        else:
            library = self.get_comment_library(library_id) if str(library_id or "").strip().isdigit() else None
            templates = self.list_library_templates(library["id"], enabled_only=True) if library and library["enabled"] else []
        videos = self.list_videos(status="pending", limit=max_count or 200)
        if max_count:
            videos = videos[: int(max_count)]
        items = []
        if accounts and library and templates:
            for index, video in enumerate(reversed(videos)):
                account = accounts[index % len(accounts)]
                template = random.choice(templates)
                items.append(
                    {
                        "video_id": video["id"],
                        "bvid": video["bvid"],
                        "url": video["url"],
                        "account_id": account["id"],
                        "account_name": account["name"],
                        "template_id": template["id"],
                        "template_text": template["text"],
                        "library_id": library["id"],
                        "library_name": library["name"],
                    }
                )
        return {
            "total": len(items),
            "items": items,
            "missing": {
                "accounts": not bool(accounts),
                "library": not bool(library and library["enabled"]),
                "templates": not bool(templates),
                "videos": not bool(videos),
            },
        }

    def record_publish_result(self, plan_item, status, error_reason="", evidence=None):
        timestamp = now_iso()
        published_at = timestamp if status == "success" else ""
        evidence = evidence if isinstance(evidence, dict) else {}
        with self.connect() as db:
            db.execute(
                """
                INSERT INTO ledger (
                    video_id, bvid, url, account_id, account_name, template_text,
                    status, error_reason, platform_comment_id, verification_method,
                    proof_text, comment_url, verified_at, published_at, created_at
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    plan_item.get("video_id"),
                    plan_item.get("bvid") or extract_bvid(plan_item.get("url")),
                    plan_item.get("url") or canonical_bili_url(plan_item.get("bvid")),
                    plan_item.get("account_id"),
                    plan_item.get("account_name") or "",
                    plan_item.get("template_text") or "",
                    status,
                    error_reason or "",
                    str(evidence.get("platform_comment_id") or ""),
                    str(evidence.get("verification_method") or ""),
                    str(evidence.get("proof_text") or ""),
                    str(evidence.get("comment_url") or ""),
                    str(evidence.get("verified_at") or ""),
                    published_at,
                    timestamp,
                ),
            )
            video_status = {
                "success": "posted",
                "submitted_unverified": "review_required",
                "skipped": "skipped",
                "login_required": "pending",
                "verify_required": "pending",
                "verify": "pending",
                "captcha_or_verify": "pending",
                "account_unavailable": "pending",
                "retryable_error": "pending",
            }.get(status, "failed")
            db.execute(
                "UPDATE videos SET status=?, last_error=?, updated_at=? WHERE id=?",
                (video_status, error_reason or "", timestamp, plan_item.get("video_id")),
            )
            if plan_item.get("account_id") and status in {"success", "submitted_unverified"}:
                today = timestamp[:10]
                self._reset_stale_daily_counts(db, today)
                db.execute(
                    """
                    UPDATE accounts
                    SET today_count=today_count+1, today_count_date=?, last_used_at=?, updated_at=?
                    WHERE id=?
                    """,
                    (today, timestamp, timestamp, plan_item.get("account_id")),
                )
            elif plan_item.get("account_id") and status in {
                "login_required",
                "verify_required",
                "verify",
                "captcha_or_verify",
            }:
                db.execute(
                    """
                    UPDATE accounts
                    SET login_status=?, login_note=?, last_login_checked_at=?, updated_at=?
                    WHERE id=?
                    """,
                    (status, error_reason or "", timestamp, timestamp, plan_item.get("account_id")),
                )

    def mark_account_cooldown(self, account_id, min_seconds=1200, max_seconds=2400):
        min_seconds = max(0, int(min_seconds or 0))
        max_seconds = max(min_seconds, int(max_seconds or min_seconds))
        delay = min_seconds if min_seconds == max_seconds else random.randint(min_seconds, max_seconds)
        next_available = (datetime.now().astimezone() + timedelta(seconds=delay)).isoformat(timespec="seconds")
        timestamp = now_iso()
        with self.connect() as db:
            db.execute(
                "UPDATE accounts SET next_available_at=?, updated_at=? WHERE id=?",
                (next_available, timestamp, account_id),
            )
        return next_available

    def update_account_login_status(self, account_id, login_status, note=""):
        status = str(login_status or "unknown").strip() or "unknown"
        timestamp = now_iso()
        with self.connect() as db:
            cursor = db.execute(
                """
                UPDATE accounts
                SET login_status=?, login_note=?, last_login_checked_at=?, updated_at=?
                WHERE id=? AND deleted=0
                """,
                (status, str(note or "").strip(), timestamp, timestamp, int(account_id)),
            )
            if cursor.rowcount <= 0:
                raise ValueError("账号不存在")
        return self.get_account(account_id)

    def _eligible_accounts(self, account_ids):
        selected = {int(value) for value in account_ids if str(value).strip().isdigit()}
        current = now_iso()
        accounts = self.list_accounts(enabled_only=True)
        result = []
        for account in accounts:
            if selected and account["id"] not in selected:
                continue
            if str(account.get("login_status") or "").strip().lower() not in {"ok", "logged_in", "success"}:
                continue
            if not str(account.get("uid") or "").strip() or not str(account.get("cookie") or "").strip():
                continue
            if account["daily_limit"] > 0 and account["today_count"] >= account["daily_limit"]:
                continue
            if account.get("next_available_at") and account["next_available_at"] > current:
                continue
            result.append(account)
        return sorted(
            result,
            key=lambda item: (
                int(item.get("today_count") or 0),
                str(item.get("last_used_at") or ""),
                int(item.get("id") or 0),
            ),
        )

    @staticmethod
    def _reset_stale_daily_counts(db, current_date=None):
        today = str(current_date or now_iso()[:10])
        db.execute(
            """
            UPDATE accounts
            SET today_count=0, today_count_date=?, updated_at=?
            WHERE deleted=0 AND today_count_date<>?
            """,
            (today, now_iso(), today),
        )

    def _account_dict(self, row):
        item = dict(row)
        item["enabled"] = bool(item["enabled"])
        item["deleted"] = bool(item.get("deleted", 0))
        return item

    def _comment_library_dict(self, row):
        item = dict(row)
        item["enabled"] = bool(item["enabled"])
        item["template_count"] = int(item.get("template_count") or 0)
        return item

    def _clean_template_lines(self, templates):
        cleaned = []
        seen = set()
        for value in templates or []:
            text = str(value or "").strip()
            if not text or text in seen:
                continue
            seen.add(text)
            cleaned.append(text)
        return cleaned

    def _release_deleted_account_name(self, db, name, account_id, timestamp):
        base_alias = f"{name}__deleted_{account_id}"
        alias = base_alias
        suffix = 1
        while db.execute("SELECT id FROM accounts WHERE name=? AND id!=?", (alias, int(account_id))).fetchone():
            suffix += 1
            alias = f"{base_alias}_{suffix}"
        db.execute("UPDATE accounts SET name=?, updated_at=? WHERE id=?", (alias, timestamp, int(account_id)))

    def _ensure_column(self, db, table, column, definition):
        columns = {row["name"] for row in db.execute(f"PRAGMA table_info({table})").fetchall()}
        if column not in columns:
            db.execute(f"ALTER TABLE {table} ADD COLUMN {column} {definition}")

    def _increment_stat(self, db, key, amount, timestamp):
        amount = int(amount or 0)
        if amount <= 0:
            return
        db.execute(
            """
            INSERT INTO daily_stats (stat_date, key, value, updated_at)
            VALUES (?, ?, ?, ?)
            ON CONFLICT(stat_date, key) DO UPDATE SET
                value=value + excluded.value,
                updated_at=excluded.updated_at
            """,
            (timestamp[:10], key, amount, timestamp),
        )
