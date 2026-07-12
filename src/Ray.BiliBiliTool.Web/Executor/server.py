from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from urllib.error import HTTPError
from urllib.parse import parse_qs, quote, urlencode, urlparse
from urllib.request import Request, urlopen
from datetime import datetime
import base64
import hashlib
import html
import json
import os
import random
import re
import shutil
import socket
import sys
import threading
import time
from types import SimpleNamespace

from account_manager import AccountDirectoryLock
from app_store import DEFAULT_DEEPSEEK_MODEL, DEFAULT_QWEN_MODEL, AppStore, seconds_until
from bili_operator import (
    LOGIN_KEYWORDS,
    STATUS_LOGIN_REQUIRED,
    STATUS_SUBMITTED_UNVERIFIED,
    STATUS_SUCCESS,
    STATUS_VERIFY,
    VERIFY_KEYWORDS,
    BiliOperator,
    launch_persistent_browser_context,
)
from config_loader import load_config
from executor_export import write_video_links_excel
from job_manager import JobError, JobManager


HOST = "127.0.0.1"
PORT = int(os.environ.get("BIBI_EXECUTOR_PORT", "8123"))
APP_VERSION = "2.0.0"
APP_STORE = None
APP_STORE_LOCK = threading.Lock()
JOBS = None
UI_SHUTDOWN_DELAY_SECONDS = 8.0
UI_SESSIONS = {}
UI_SESSION_LOCK = threading.Lock()
UI_SHUTDOWN_TIMER = None
WBI_MIXIN_KEY_ENC_TAB = [
    46, 47, 18, 2, 53, 8, 23, 32,
    15, 50, 10, 31, 58, 3, 45, 35,
    27, 43, 5, 49, 33, 9, 42, 19,
    29, 28, 14, 39, 12, 38, 41, 13,
    37, 48, 7, 16, 24, 55, 40, 61,
    26, 17, 0, 1, 60, 51, 30, 4,
    22, 25, 54, 21, 56, 59, 6, 63,
    57, 62, 11, 36, 20, 34, 44, 52,
]
WBI_KEY_CACHE = {"img_key": "", "sub_key": "", "expires_at": 0}
KEYWORD_NORMALIZE_LIMIT = 6
DISCOVERY_DEFAULT_QUEUE_TARGET = 10
DISCOVERY_MAX_QUEUE_TARGET = 50
DISCOVERY_PAGE_SIZE = 20
DISCOVERY_MAX_PAGES = 60
DISCOVERY_FALLBACK_TERMS = (
    "\u8033\u673a",
    "\u6d4b\u8bc4",
    "\u8bc4\u6d4b",
    "\u6a2a\u8bc4",
    "\u63a8\u8350",
    "\u964d\u566a",
    "\u84dd\u7259",
    "\u6e38\u620f\u8033\u673a",
)
KEYWORD_SPLIT_RE = re.compile(r"[\n\r,，;；、|/／]+")
KEYWORD_PREFIX_RE = re.compile(
    r"^\s*(?:[-*•·]+|\d+\s*[.)、．]\s*|[（(]\d+[）)]\s*|[一二三四五六七八九十百]+[、.．]\s*)"
)
KEYWORD_EDGE_CHARS = " \t\r\n,，;；、。.!！?？:：\"'“”‘’[]【】()（）<>《》"


class ExclusiveThreadingHTTPServer(ThreadingHTTPServer):
    allow_reuse_address = False
    allow_reuse_port = False
    daemon_threads = True

    def server_bind(self):
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()


def process_is_alive(process_id):
    try:
        process_id = int(process_id)
    except (TypeError, ValueError):
        return False
    if process_id <= 0:
        return False

    if os.name == "nt":
        import ctypes
        from ctypes import wintypes

        process_query_limited_information = 0x1000
        still_active = 259
        error_access_denied = 5
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
        kernel32.OpenProcess.restype = wintypes.HANDLE
        kernel32.GetExitCodeProcess.argtypes = (wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD))
        kernel32.GetExitCodeProcess.restype = wintypes.BOOL
        kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
        kernel32.CloseHandle.restype = wintypes.BOOL

        handle = kernel32.OpenProcess(process_query_limited_information, False, process_id)
        if not handle:
            return ctypes.get_last_error() == error_access_denied
        try:
            exit_code = wintypes.DWORD()
            if not kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code)):
                return False
            return exit_code.value == still_active
        finally:
            kernel32.CloseHandle(handle)

    try:
        os.kill(process_id, 0)
        return True
    except PermissionError:
        return True
    except OSError:
        return False


def monitor_parent_process(httpd, process_id):
    while True:
        time.sleep(2)
        if process_is_alive(process_id):
            continue
        httpd.shutdown()
        return


def _bundle_dir():
    return os.path.abspath(getattr(sys, "_MEIPASS", os.path.dirname(os.path.abspath(__file__))))


def _app_dir():
    configured = os.environ.get("BILI_WORKBENCH_HOME")
    if configured:
        return os.path.abspath(configured)
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))


BUNDLE_DIR = _bundle_dir()
APP_DIR = _app_dir()


def _session_id_from_payload(payload):
    session_id = str((payload or {}).get("sessionId") or "").strip()
    return session_id or f"ui-{time.time_ns()}"


def _session_token_from_payload(payload):
    return str((payload or {}).get("sessionToken") or "").strip()[:128]


def _cancel_ui_shutdown_locked():
    global UI_SHUTDOWN_TIMER
    if UI_SHUTDOWN_TIMER:
        UI_SHUTDOWN_TIMER.cancel()
        UI_SHUTDOWN_TIMER = None


def request_server_shutdown(httpd):
    httpd.shutdown()


def _shutdown_if_no_ui_sessions(httpd):
    global UI_SHUTDOWN_TIMER
    with UI_SESSION_LOCK:
        UI_SHUTDOWN_TIMER = None
        if UI_SESSIONS:
            return
    request_server_shutdown(httpd)


def register_ui_session(payload):
    session_id = _session_id_from_payload(payload)
    session_token = _session_token_from_payload(payload)
    with UI_SESSION_LOCK:
        tracked = bool(session_token)
        if tracked:
            UI_SESSIONS[session_id] = {"token": session_token, "lastSeen": time.time()}
            _cancel_ui_shutdown_locked()
        active_sessions = len(UI_SESSIONS)
    return {
        "ok": True,
        "sessionId": session_id,
        "tracked": tracked,
        "activeSessions": active_sessions,
        "shutdownScheduled": False,
    }


def close_ui_session(payload, httpd):
    global UI_SHUTDOWN_TIMER
    session_id = _session_id_from_payload(payload)
    session_token = _session_token_from_payload(payload)
    with UI_SESSION_LOCK:
        session = UI_SESSIONS.get(session_id)
        known_session = session is not None
        stored_token = session.get("token", "") if isinstance(session, dict) else ""
        token_matched = known_session and bool(stored_token) and session_token == stored_token
        if token_matched:
            UI_SESSIONS.pop(session_id, None)
        active_sessions = len(UI_SESSIONS)
        shutdown_scheduled = token_matched and active_sessions == 0
        if shutdown_scheduled:
            _cancel_ui_shutdown_locked()
            UI_SHUTDOWN_TIMER = threading.Timer(UI_SHUTDOWN_DELAY_SECONDS, _shutdown_if_no_ui_sessions, args=(httpd,))
            UI_SHUTDOWN_TIMER.daemon = True
            UI_SHUTDOWN_TIMER.start()
    return {
        "ok": True,
        "sessionId": session_id,
        "knownSession": known_session,
        "tokenMatched": token_matched,
        "activeSessions": active_sessions,
        "shutdownScheduled": shutdown_scheduled,
        "shutdownDelaySeconds": UI_SHUTDOWN_DELAY_SECONDS if shutdown_scheduled else 0,
    }


def reset_ui_sessions_for_tests():
    with UI_SESSION_LOCK:
        UI_SESSIONS.clear()
        _cancel_ui_shutdown_locked()


def load_app_config():
    external_config = os.path.join(APP_DIR, "config.yaml")
    if os.path.exists(external_config):
        return load_config(external_config)
    return load_config(os.path.join(BUNDLE_DIR, "config.yaml"))


def first_existing_resource(relative_path):
    path = os.path.normpath(str(relative_path or ""))
    if os.path.isabs(path):
        return path if os.path.exists(path) else ""
    for base_dir in (APP_DIR, BUNDLE_DIR):
        candidate = os.path.join(base_dir, path)
        if os.path.exists(candidate):
            return candidate
    return os.path.join(APP_DIR, path)


def first_existing_directory(path_value):
    path = os.path.normpath(str(path_value or ""))
    if not path:
        return ""
    if os.path.isabs(path):
        return path if os.path.isdir(path) else ""
    for base_dir in (APP_DIR, BUNDLE_DIR):
        candidate = os.path.join(base_dir, path)
        if os.path.isdir(candidate):
            return candidate
    return os.path.join(APP_DIR, path)


def app_now():
    return datetime.now().astimezone().isoformat(timespec="seconds")


def get_store():
    global APP_STORE
    if APP_STORE is not None:
        return APP_STORE
    with APP_STORE_LOCK:
        if APP_STORE is not None:
            return APP_STORE
        config = load_app_config()
        store = AppStore(os.path.join(APP_DIR, "data", "app.db"))
        store.initialize()
        store.sync_accounts_from_config(config, base_dir=APP_DIR)
        store.rebase_account_directories(APP_DIR)
        template_dir = first_existing_directory(config.get("comment", {}).get("template_dir", ""))
        if not store.list_comment_libraries(enabled_only=True) and template_dir and os.path.isdir(template_dir):
            store.import_comment_libraries_from_directory(template_dir)
        APP_STORE = store
    return APP_STORE


def get_jobs():
    global JOBS
    if JOBS is None:
        JOBS = JobManager(get_store())
    return JOBS


def fallback_comment_from_library(library_id):
    if not str(library_id or "").strip().isdigit():
        return ""
    library = get_store().get_comment_library(library_id)
    if not library or not library.get("enabled"):
        return ""
    templates = get_store().list_library_templates(library["id"], enabled_only=True)
    choices = [str(item.get("text") or "").strip() for item in templates if str(item.get("text") or "").strip()]
    return random.choice(choices) if choices else ""


def remove_managed_account_profile(account):
    profile_path = os.path.realpath(str((account or {}).get("user_data_dir") or ""))
    accounts_root = os.path.realpath(os.path.join(APP_DIR, "accounts"))
    if not profile_path or profile_path == accounts_root:
        return
    try:
        if os.path.commonpath([profile_path, accounts_root]) == accounts_root:
            shutil.rmtree(profile_path, ignore_errors=True)
    except ValueError:
        return


def extract_bvid(value):
    match = re.search(r"BV[a-zA-Z0-9]+", value or "")
    return match.group(0) if match else ""


def clean_text(value):
    text = re.sub(r"<[^>]+>", "", value or "")
    return re.sub(r"\s+", " ", text).strip()


def timestamp_year(value):
    try:
        return datetime.fromtimestamp(int(value)).year
    except Exception:
        return None


def bili_search_item_to_candidate(item, keyword, page, source, target_year=None):
    bvid = item.get("bvid") or ""
    if not bvid:
        return None
    pub_year = timestamp_year(item.get("pubdate"))
    if target_year and pub_year != target_year:
        return None
    return {
        "bvid": bvid,
        "url": f"https://www.bilibili.com/video/{bvid}",
        "title": clean_text(item.get("title") or ""),
        "author": clean_text(item.get("author") or ""),
        "description": clean_text(item.get("description") or ""),
        "category": clean_text(item.get("typename") or ""),
        "keyword": keyword,
        "play": item.get("play"),
        "favorites": item.get("favorites"),
        "pubdate": item.get("pubdate"),
        "year": pub_year,
        "search_page": page,
        "source": source,
    }


def bili_request_headers(referer="https://www.bilibili.com/"):
    return {
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        "(KHTML, like Gecko) Chrome/125.0 Safari/537.36",
        "Referer": referer,
        "Accept": "application/json,text/plain,text/html,*/*",
        "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
        "Connection": "keep-alive",
        "Origin": "https://www.bilibili.com",
        "Sec-Fetch-Dest": "empty",
        "Sec-Fetch-Mode": "cors",
        "Sec-Fetch-Site": "same-site",
    }


def bili_referer(referer_bvid=""):
    if referer_bvid and str(referer_bvid).startswith("http"):
        return str(referer_bvid)
    return f"https://www.bilibili.com/video/{referer_bvid}" if referer_bvid else "https://www.bilibili.com/"


def bili_get_json(url, referer_bvid=""):
    request = Request(url, headers=bili_request_headers(bili_referer(referer_bvid)))
    with urlopen(request, timeout=12) as response:
        return json.loads(response.read().decode("utf-8"))


def bili_get_text(url, referer="https://www.bilibili.com/"):
    request = Request(url, headers=bili_request_headers(referer))
    with urlopen(request, timeout=12) as response:
        charset = response.headers.get_content_charset() or "utf-8"
        return response.read().decode(charset, errors="ignore")


def extract_wbi_key(url):
    filename = str(url or "").rsplit("/", 1)[-1]
    return filename.split(".", 1)[0]


def get_wbi_keys():
    now = time.time()
    if WBI_KEY_CACHE.get("img_key") and WBI_KEY_CACHE.get("sub_key") and WBI_KEY_CACHE.get("expires_at", 0) > now:
        return WBI_KEY_CACHE["img_key"], WBI_KEY_CACHE["sub_key"]
    data = bili_get_json("https://api.bilibili.com/x/web-interface/nav", "https://www.bilibili.com/")
    wbi_img = ((data.get("data") or {}).get("wbi_img") or {})
    img_key = extract_wbi_key(wbi_img.get("img_url"))
    sub_key = extract_wbi_key(wbi_img.get("sub_url"))
    if not img_key or not sub_key:
        raise ValueError("B站 WBI key 获取失败")
    WBI_KEY_CACHE.update({"img_key": img_key, "sub_key": sub_key, "expires_at": now + 6 * 60 * 60})
    return img_key, sub_key


def wbi_mixin_key(img_key, sub_key):
    raw = (img_key or "") + (sub_key or "")
    return "".join(raw[index] for index in WBI_MIXIN_KEY_ENC_TAB if index < len(raw))[:32]


def wbi_signed_params(params):
    img_key, sub_key = get_wbi_keys()
    signed = {key: value for key, value in (params or {}).items() if value not in [None, ""]}
    signed["wts"] = int(time.time())
    for key, value in list(signed.items()):
        text = str(value)
        signed[key] = re.sub(r"[!'()*]", "", text)
    query = urlencode(sorted(signed.items()))
    signed["w_rid"] = hashlib.md5((query + wbi_mixin_key(img_key, sub_key)).encode("utf-8")).hexdigest()
    return signed


def search_bili_videos_from_wbi(keyword, page=1, limit=8, target_year=None):
    params = wbi_signed_params(
        {
            "search_type": "video",
            "keyword": keyword,
            "page": page,
            "order": "pubdate",
        }
    )
    search_url = "https://api.bilibili.com/x/web-interface/wbi/search/type?" + urlencode(params)
    data = bili_get_json(search_url, f"https://search.bilibili.com/all?keyword={quote(keyword)}")
    if data.get("code") != 0:
        return []
    results = []
    for item in ((data.get("data") or {}).get("result") or []):
        candidate = bili_search_item_to_candidate(item, keyword, page, "wbi_api", target_year)
        if candidate:
            results.append(candidate)
        if len(results) >= limit:
            break
    return results


def decode_js_string(value):
    text = str(value or "")
    try:
        return json.loads(f'"{text}"')
    except Exception:
        return text.replace("\\/", "/")


def search_bili_videos_from_html(keyword, page=1, limit=8):
    search_url = (
        "https://search.bilibili.com/all"
        f"?keyword={quote(keyword)}&page={page}&order=pubdate&from_source=webtop_search"
    )
    body = bili_get_text(search_url, "https://www.bilibili.com/")
    items = []
    seen = set()
    matches = []
    for match in re.finditer(r'"bvid"\s*:\s*"(BV[a-zA-Z0-9]+)"(?:(?!\{).){0,1800}?"title"\s*:\s*"(.*?)"', body, re.S):
        matches.append((match.start(), match.group(1), clean_text(html.unescape(decode_js_string(match.group(2))))))
    for match in re.finditer(r'(?:https?:)?//www\.bilibili\.com/video/(BV[a-zA-Z0-9]+)', body):
        matches.append((match.start(), match.group(1), keyword))
    for _position, bvid, title in sorted(matches, key=lambda item: item[0]):
        if bvid in seen:
            continue
        seen.add(bvid)
        items.append(
            {
                "bvid": bvid,
                "url": f"https://www.bilibili.com/video/{bvid}",
                "title": title or keyword,
                "author": "",
                "description": "",
                "category": "",
                "keyword": keyword,
                "play": None,
                "favorites": None,
                "pubdate": None,
                "year": None,
                "search_page": page,
                "source": "web_fallback",
            }
        )
        if len(items) >= limit:
            break
    return items


def search_bili_videos_page(keyword, page=1, limit=DISCOVERY_PAGE_SIZE, year=""):
    page = max(1, int(page or 1))
    limit = max(1, int(limit or DISCOVERY_PAGE_SIZE))
    target_year = int(year) if str(year or "").isdigit() else None
    first_error = None
    try:
        wbi_items = search_bili_videos_from_wbi(keyword, page, limit, target_year)
    except HTTPError as error:
        first_error = first_error or error
        wbi_items = None
    except Exception:
        wbi_items = None
    if wbi_items:
        return wbi_items[:limit]

    search_url = (
        "https://api.bilibili.com/x/web-interface/search/type"
        f"?search_type=video&keyword={quote(keyword)}&page={page}&order=pubdate"
    )
    try:
        data = bili_get_json(search_url, f"https://search.bilibili.com/all?keyword={quote(keyword)}")
    except HTTPError as error:
        first_error = first_error or error
        try:
            fallback_items = search_bili_videos_from_html(keyword, page, limit)
        except Exception:
            fallback_items = []
        if fallback_items:
            return fallback_items[:limit]
        raise first_error
    if data.get("code") != 0:
        return []
    results = []
    for item in ((data.get("data") or {}).get("result") or []):
        candidate = bili_search_item_to_candidate(item, keyword, page, "api", target_year)
        if candidate:
            results.append(candidate)
        if len(results) >= limit:
            break
    return results


def search_bili_videos(keywords, per_keyword=8, year="", max_pages=5):
    results = []
    seen = set()
    first_error = None
    for keyword in keywords[:6]:
        keyword_added = 0
        for page in range(1, max(1, int(max_pages or 1)) + 1):
            if keyword_added >= per_keyword:
                break
            try:
                page_items = search_bili_videos_page(keyword, page, per_keyword - keyword_added, year)
            except HTTPError as error:
                first_error = first_error or error
                break
            if not page_items:
                break
            for item in page_items:
                bvid = item.get("bvid") or ""
                if not bvid or bvid in seen:
                    continue
                seen.add(bvid)
                keyword_added += 1
                results.append(item)
                if keyword_added >= per_keyword:
                    break
    if not results and first_error:
        raise first_error
    return results


def bili_http_error_to_job_error(error, action="搜索"):
    code = getattr(error, "code", "")
    reason = getattr(error, "reason", "") or getattr(error, "msg", "")
    if code in {403, 412, 429}:
        return JobError(
            f"B站{action}接口被临时拒绝，通常是请求过快、公开接口风控或当前网络环境触发。",
            category="api_error",
            recoverable=True,
            suggestion="稍后重试，或把每轮搜索间隔调大、减少关键词和本轮最多发现数量后再运行。",
        )
    return JobError(
        f"B站{action}接口请求失败：HTTP {code} {reason}".strip(),
        category="api_error",
        recoverable=True,
        suggestion="检查网络连接，稍后重试；如果连续失败，请降低搜索频率。",
    )


def search_bili_videos_with_browser_accounts(keywords, per_keyword=8, year="", max_pages=2):
    accounts = [
        account
        for account in get_store().list_accounts(enabled_only=True)
        if str(account.get("login_status") or "").strip().lower() in {"ok", "logged_in", "success"}
        and str(account.get("uid") or "").strip()
        and str(account.get("cookie") or "").strip()
    ]
    if not accounts:
        return []
    config = load_app_config()
    operator = BiliOperator(config.get("browser", {}), config.get("comment", {}))
    results = []
    seen = set()
    last_error = None
    for account in accounts:
        account_obj = SimpleNamespace(name=account["name"], user_data_dir=account["user_data_dir"])
        try:
            items = operator.search_videos_with_browser(
                account_obj,
                keywords,
                per_keyword=per_keyword,
                year=year,
                max_pages=max_pages,
            )
        except Exception as error:
            last_error = error
            continue
        for item in items:
            bvid = item.get("bvid") or ""
            if not bvid or bvid in seen:
                continue
            seen.add(bvid)
            results.append(item)
            if len(results) >= max(1, int(per_keyword or 1)) * max(1, min(6, len(keywords or []))):
                return results
        if results:
            return results
    if last_error:
        raise JobError(
            f"账号浏览器搜索兜底失败：{last_error}",
            category="api_error",
            recoverable=True,
            suggestion="确认至少一个账号浏览器可以正常打开 B站搜索页；如果出现验证码或访问异常，请先人工处理后再运行。",
        )
    return results


def fetch_bili_video(video_url):
    bvid = extract_bvid(video_url)
    if not bvid:
        raise ValueError("没有识别到 BV 号")

    view = bili_get_json(f"https://api.bilibili.com/x/web-interface/view?bvid={bvid}", bvid)
    if view.get("code") != 0:
        raise ValueError(view.get("message") or "视频信息接口返回异常")

    data = view.get("data") or {}
    aid = data.get("aid")

    return {
        "bvid": bvid,
        "aid": aid,
        "title": data.get("title") or "",
        "author": (data.get("owner") or {}).get("name") or "",
        "description": data.get("desc") or "",
        "publishedAt": data.get("pubdate"),
        "category": data.get("tname") or "",
        "comments": [],
        "source": "bilibili",
    }


def read_json_body(handler):
    length = int(handler.headers.get("Content-Length") or 0)
    if length:
        body = handler.rfile.read(length)
    elif "chunked" in str(handler.headers.get("Transfer-Encoding") or "").lower():
        chunks = []
        while True:
            size_line = handler.rfile.readline()
            if not size_line:
                raise ValueError("请求体分块不完整")
            size_token = size_line.split(b";", 1)[0].strip()
            try:
                chunk_size = int(size_token, 16)
            except ValueError as error:
                raise ValueError("请求体分块长度无效") from error
            if chunk_size == 0:
                while True:
                    trailer = handler.rfile.readline()
                    if trailer in (b"", b"\r\n", b"\n"):
                        break
                break
            chunk = handler.rfile.read(chunk_size)
            if len(chunk) != chunk_size:
                raise ValueError("请求体分块内容不完整")
            chunks.append(chunk)
            if handler.rfile.read(2) != b"\r\n":
                raise ValueError("请求体分块格式无效")
        body = b"".join(chunks)
    else:
        return {}

    return json.loads(body.decode("utf-8")) if body else {}


def library_id_from_payload(payload):
    return (payload or {}).get("libraryId") or (payload or {}).get("library_id") or (payload or {}).get("commentLibraryId")


def libraries_payload():
    libraries = []
    for library in get_store().list_comment_libraries():
        libraries.append(
            {
                **library,
                "templates": get_store().list_library_templates(library["id"], enabled_only=True),
            }
        )
    return libraries


def read_multipart_images(handler):
    images, _fields = read_multipart_form(handler)
    return images


def read_multipart_form(handler):
    content_type = handler.headers.get("Content-Type") or ""
    match = re.search(r"boundary=([^;]+)", content_type)
    if not match:
        raise ValueError("上传格式异常：没有 multipart boundary")
    boundary = match.group(1).strip().strip('"').encode("utf-8")
    length = int(handler.headers.get("Content-Length") or 0)
    if not length:
        return [], {}
    raw = handler.rfile.read(length)
    parts = raw.split(b"--" + boundary)
    images = []
    fields = {}
    for part in parts:
        part = part.strip()
        if not part or part == b"--":
            continue
        if part.endswith(b"--"):
            part = part[:-2].strip()
        header_blob, sep, data = part.partition(b"\r\n\r\n")
        if not sep:
            continue
        headers = header_blob.decode("utf-8", errors="ignore")
        name_match = re.search(r'name="([^"]+)"', headers)
        field_name = name_match.group(1) if name_match else ""
        filename_match = re.search(r'filename="([^"]*)"', headers)
        data = data.rstrip(b"\r\n")
        if field_name == "images" and filename_match and data:
            filename = os.path.basename(filename_match.group(1) if filename_match else "comment.png")
            images.append((filename, data))
        elif field_name:
            fields[field_name] = data.decode("utf-8", errors="ignore").strip()
    return images, fields


def parse_ai_response(provider, raw_body):
    text = raw_body.decode("utf-8", errors="ignore").strip()
    if not text:
        raise RuntimeError(f"{provider} 返回空响应，请检查 API Key、余额、模型名称或网络连接。")
    try:
        return json.loads(text)
    except json.JSONDecodeError as error:
        raise RuntimeError(f"{provider} 返回了非 JSON 响应，请检查 API Key、余额、模型名称或网络连接。") from error


def parse_ai_content(provider, content):
    text = str(content or "").strip()
    if not text:
        raise RuntimeError(f"{provider} 模型返回空内容，请检查模型名称或稍后重试。")
    try:
        return json.loads(text)
    except json.JSONDecodeError as error:
        raise RuntimeError(f"{provider} 模型没有按 JSON 返回，请重试或切换备用模型。") from error


def chat_completions_url(base_url, default_base_url):
    root = str(base_url or default_base_url or "").strip().rstrip("/")
    if not root:
        root = str(default_base_url).rstrip("/")
    if root.endswith("/chat/completions"):
        return root
    return f"{root}/chat/completions"


def deepseek_chat(api_key, model, messages, max_tokens=1200, json_mode=True, base_url=""):
    resolved_model = model or DEFAULT_DEEPSEEK_MODEL
    payload = {
        "model": resolved_model,
        "messages": messages,
        "temperature": 0.3,
        "max_tokens": max_tokens,
    }
    if resolved_model.startswith("deepseek-v4-"):
        payload["thinking"] = {"type": "disabled"}
    if json_mode:
        payload["response_format"] = {"type": "json_object"}
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = Request(
        chat_completions_url(base_url, "https://api.deepseek.com"),
        data=body,
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
            "Accept": "application/json",
        },
        method="POST",
    )
    try:
        with urlopen(request, timeout=30) as response:
            result = parse_ai_response("DeepSeek", response.read())
    except HTTPError as error:
        detail = error.read().decode("utf-8", errors="ignore")
        raise ValueError(f"DeepSeek 接口返回 {error.code}：{detail[:800]}") from error
    content = result["choices"][0]["message"]["content"]
    if not json_mode:
        return str(content or "").strip()
    return parse_ai_content("DeepSeek", content)


def qwen_chat(api_key, model, messages, max_tokens=3000, json_mode=True, base_url=""):
    payload = {
        "model": model or DEFAULT_QWEN_MODEL,
        "messages": messages,
        "temperature": 0.1,
        "max_tokens": max_tokens,
    }
    if json_mode:
        payload["response_format"] = {"type": "json_object"}
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = Request(
        chat_completions_url(base_url, "https://dashscope.aliyuncs.com/compatible-mode/v1"),
        data=body,
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
            "Accept": "application/json",
        },
        method="POST",
    )
    try:
        with urlopen(request, timeout=90) as response:
            result = parse_ai_response("千问", response.read())
    except HTTPError as error:
        detail = error.read().decode("utf-8", errors="ignore")
        raise ValueError(f"千问接口返回 {error.code}：{detail[:800]}") from error
    content = result["choices"][0]["message"]["content"]
    if not json_mode:
        return str(content or "").strip()
    return parse_ai_content("千问", content)


def generate_ai_comment(provider, video_title, rules, settings=None):
    provider = str(provider or "deepseek").strip().lower()
    settings = settings or get_store().get_settings(include_secrets=True)
    rules = str(rules or "请生成一条字数少于30字的自然评论。").strip()
    video_title = str(video_title or "").strip()

    if provider == "deepseek":
        config = settings.get("deepseek") or {}
        api_key = config.get("apiKey")
        if not api_key:
            raise ValueError("请先在设置中填写 DeepSeek API Key")
        comment = deepseek_chat(
            api_key,
            config.get("model") or DEFAULT_DEEPSEEK_MODEL,
            [
                {
                    "role": "system",
                    "content": (
                        "请扮演B站用户，根据视频标题与规则生成一条简短、自然的评论。"
                        "只输出评论正文，不要引言、列表、代码块或解释。"
                        f"规则：{rules}"
                    ),
                },
                {"role": "user", "content": f"视频标题：{video_title}"},
            ],
            max_tokens=180,
            json_mode=False,
            base_url=config.get("baseUrl"),
        )
    elif provider == "qwen":
        config = settings.get("qwen") or {}
        api_key = config.get("apiKey")
        if not api_key:
            raise ValueError("请先在设置中填写通义千问 API Key")
        comment = qwen_chat(
            api_key,
            config.get("model") or DEFAULT_QWEN_MODEL,
            [
                {
                    "role": "user",
                    "content": (
                        "请扮演B站用户，根据视频标题与规则生成一条简短、自然的评论。"
                        "只输出评论正文，不要引言、列表、代码块或解释。"
                        f"规则：{rules}。视频标题：{video_title}"
                    ),
                }
            ],
            max_tokens=180,
            json_mode=False,
            base_url=config.get("baseUrl"),
        )
    else:
        raise ValueError("不支持的评论生成方式")

    comment = str(comment or "").strip().strip('"').strip("'").strip()
    if not comment:
        raise ValueError("AI 未返回可用评论")
    return comment


def image_mime_type(filename):
    ext = os.path.splitext(filename or "")[1].lower()
    if ext in [".jpg", ".jpeg"]:
        return "image/jpeg"
    if ext == ".webp":
        return "image/webp"
    if ext == ".bmp":
        return "image/bmp"
    return "image/png"


def build_qwen_comment_ocr_messages(images, custom_rules=""):
    if not images:
        raise ValueError("请先上传评论区截图")
    content = [
        {
            "type": "text",
            "text": """
你是一个 B站评论区截图识别与整理助手。请识别用户上传的评论区截图，把其中真实评论整理成可编辑 txt。

必须遵守：
1. 只根据截图内容识别，不要编造评论，不要生成营销评论。
2. 区分用户名、时间、点赞数、UP 标识、按钮、分页、排序、图标残留和真实评论内容；输出里不要包含用户名、时间、点赞数、按钮文案。
3. 尽量保留主评论与回复关系。主评论用 [主评] 开头；回复另起一行，前面缩进两个空格并用 [回复] 开头。
4. 如果某条回复能看出是在回复哪条主评，就放在对应主评下面；如果无法确定，就作为 [主评] 输出。
5. 合并被截图换行或 OCR 拆碎的同一条评论；删除明显乱码和无意义符号。
6. 不要润色评论观点，不要替评论补事实。
7. 输出必须是 JSON，且只输出 JSON。

JSON 字段：
{
  "text": "制式评论结构 txt",
  "comment_count": 数字,
  "notes": "识别与整理说明"
}
""".strip(),
        }
    ]
    custom_rules = (custom_rules or "").strip()
    if custom_rules:
        content[0]["text"] += "\n\n以下是用户自定义识别规则，必须优先执行：\n" + custom_rules
    for filename, data in images[:8]:
        encoded = base64.b64encode(data).decode("ascii")
        content.append(
            {
                "type": "image_url",
                "image_url": {
                    "url": f"data:{image_mime_type(filename)};base64,{encoded}"
                },
            }
        )
    return [{"role": "user", "content": content}]


def build_deepseek_messages(payload):
    custom_rules = (payload.get("custom_rules") or "").strip()
    system = """
你是一个评论草稿和回复策略助手。你只做推荐，不发布评论。
你必须遵守：
1. 不得声称自己购买、使用、体验过产品，除非输入材料明确提供。
2. 不得冒充路人、用户、博主、UP主。
3. 不得编造产品参数、价格、效果、售后承诺。
4. 不得使用夸张营销语、刷屏话术、攻击或引战内容。
5. 必须判断建议动作：main_comment、reply_to_comment、skip 三选一。
6. 如果建议 reply_to_comment，target_comment 必须来自输入评论样本中的主评。
7. 可以根据评论语境生成自然草稿。若输入 feedbacks 为空，则按评论区语境和用户自定义规则生成；不得新增未确认事实。
8. 如果用户提供了自定义规则，必须优先遵守。
9. 如果不是 skip，必须按评论区上下文和用户自定义规则生成候选评论草稿；如果用户自定义规则指定数量，则按该数量生成。
10. 草稿必须适合人工审核后使用，不得要求或暗示自动发布。
11. 必须先阅读 video.raw_comment_text、video.comment_summary 和 comment_threads，提炼评论区正在讨论的具体语境，再生成草稿。
12. 每条草稿必须至少贴合一个输入评论区里出现过的具体讨论点，例如佩戴、降噪、接口、游戏、通勤、价格、型号对比等；不得只围绕产品名生成泛泛问题。
13. 如果评论区语境不足、识别文本为空，或者无法判断适合参与的角度，suggested_action 必须为 skip，并在 reason 中说明“缺少可用评论区语境”。
14. 每条 draft_comments.reason 必须说明这条草稿使用了哪一个评论区语境点，不能只写“符合规则”。
15. 输出必须是 JSON，且只输出 JSON。

JSON 字段：
suggested_action: main_comment | reply_to_comment | skip
target_comment: 字符串或 null
target_comment_type: main_comment 或 null
recommended_feedback_id: 字符串或 null
draft_comment: 字符串或 null，兼容字段，填入第一条候选
draft_comments: 数组，数量由用户自定义规则决定；如果用户未指定数量，则生成 1-3 条。每条包含：
  text: 字符串
  action: main_comment | reply_to_comment | skip
  risk_level: 低 | 中 | 高
  reason: 字符串，必须写明使用了哪个评论区语境点
match_score: 0-100 数字
risk_level: 低 | 中 | 高
reason: 字符串，概括本次生成依据的评论区语境
review_notes: 字符串
violations_check: {
  claims_personal_experience: boolean,
  contains_unverified_specs: boolean,
  pretends_to_be_user: boolean,
  too_promotional: boolean
}
""".strip()
    if custom_rules:
        system += "\n\n以下是用户自定义规则，优先级高于普通风格建议：\n" + custom_rules
    user = {
        "video": payload.get("video") or {},
        "comment_threads": payload.get("comment_threads") or [],
        "feedbacks": payload.get("feedbacks") or [],
    }
    return [
        {"role": "system", "content": system},
        {"role": "user", "content": json.dumps(user, ensure_ascii=False)},
    ]


def build_discovery_messages(payload, candidates):
    custom_rules = (payload.get("custom_rules") or "").strip()
    search_rules = (payload.get("search_rules") or "").strip()
    keywords = payload.get("keywords") or []
    year = payload.get("year") or ""
    limit = payload.get("limit") or 5
    system = """
你是一个 B站耳机相关视频发现助手，只负责筛选候选视频，不负责生成评论，不发布任何内容。
你必须遵守：
1. 只保留适合后续人工审核的视频，明显不相关的视频不要输出。
2. 优先选择耳机单评、横评、推荐合集、游戏耳机、降噪耳机、通勤耳机相关视频。
3. 不要输出“已过滤/不适合”的项目；不合适就直接不返回。
4. 不得编造视频标题、UP主、链接、BV号，只能使用输入候选里的内容。
5. 必须优先遵守用户设置的搜索年份、目标条数和搜索规则。
6. status 只能是“值得跟进”或“待判断”。高相关、方向明确用“值得跟进”；信息不足但可能相关用“待判断”。
7. type 使用：单品测评、横评、推荐合集、游戏耳机、降噪耳机、通勤耳机、其他 之一。
8. 输出 items 不得超过目标条数。
9. 输出必须是 JSON，且只输出 JSON。

JSON 字段：
items: [
  {
    bvid: 字符串,
    url: 字符串,
    title: 字符串,
    author: 字符串,
    type: 字符串,
    keywords: 字符串数组,
    status: 值得跟进 | 待判断,
    score: 0-100 数字,
    reason: 字符串,
    notes: 字符串
  }
]
""".strip()
    if custom_rules:
        system += "\n\n以下是用户自定义规则，优先级高于普通筛选建议：\n" + custom_rules
    if search_rules:
        system += "\n\n以下是本次视频搜索规则，必须优先执行：\n" + search_rules
    user = {
        "monitor_keywords": keywords,
        "target_year": year,
        "result_limit": limit,
        "candidates": candidates,
    }
    return [
        {"role": "system", "content": system},
        {"role": "user", "content": json.dumps(user, ensure_ascii=False)},
    ]


def filter_discovery_items(result, candidates, existing, limit=20):
    by_bvid = {item.get("bvid"): item for item in candidates}
    existing_text = " ".join(existing or "")
    filtered = []
    for item in result.get("items") or []:
        bvid = item.get("bvid") or extract_bvid(item.get("url") or "")
        if not bvid or bvid not in by_bvid or bvid in existing_text:
            continue
        source = by_bvid[bvid]
        status = item.get("status") if item.get("status") in ["值得跟进", "待判断"] else "待判断"
        filtered.append(
            {
                "bvid": bvid,
                "url": source.get("url"),
                "title": source.get("title"),
                "author": source.get("author"),
                "type": item.get("type") or source.get("category") or "其他",
                "keywords": item.get("keywords") or [source.get("keyword")],
                "status": status,
                "score": item.get("score") or 0,
                "reason": item.get("reason") or "",
                "notes": item.get("notes") or "",
            }
        )
    try:
        max_items = max(1, min(20, int(limit)))
    except Exception:
        max_items = 5
    return filtered[:max_items]


def _truthy(value, default=False):
    if value is None:
        return default
    if isinstance(value, str):
        return value.strip().lower() in {"1", "true", "yes", "on", "y", "是", "开启"}
    return bool(value)


def _payload_urls(payload):
    urls = payload.get("urls") or payload.get("items") or []
    if isinstance(urls, str):
        return [item.strip() for item in re.split(r"[\s,，]+", urls) if item.strip()]
    if isinstance(urls, list):
        result = []
        for item in urls:
            if isinstance(item, dict):
                value = item.get("video_url") or item.get("url") or ""
            else:
                value = item
            value = str(value or "").strip()
            if value:
                result.append(value)
        return result
    return []


def _account_id_from_path(path):
    match = re.match(r"^/api/accounts/(\d+)$", path or "")
    return int(match.group(1)) if match else None


def _comment_library_id_from_path(path):
    match = re.match(r"^/api/comment-libraries/(\d+)$", path or "")
    return int(match.group(1)) if match else None


def _video_id_from_path(path):
    match = re.match(r"^/api/videos/(\d+)$", path or "")
    return int(match.group(1)) if match else None


def _search_rules(payload, settings):
    return (
        payload.get("rules")
        or payload.get("search_rules")
        or payload.get("custom_rules")
        or settings.get("defaultSearchRules")
        or ""
    )


def _discovery_payload(payload, settings, candidates, limit):
    data = dict(payload or {})
    data["limit"] = limit
    data["search_rules"] = _search_rules(payload or {}, settings)
    data["custom_rules"] = data.get("custom_rules") or ""
    return build_discovery_messages(data, candidates)


def exclude_existing_candidates(candidates, existing):
    existing_set = {str(item or "") for item in existing or []}
    filtered = []
    for item in candidates or []:
        bvid = item.get("bvid") or extract_bvid(item.get("url") or "")
        if bvid and bvid in existing_set:
            continue
        filtered.append(item)
    return filtered


def filter_candidates_with_ai(payload, candidates, limit):
    settings = get_store().get_settings(include_secrets=True)
    deepseek = settings.get("deepseek") or {}
    qwen = settings.get("qwen") or {}
    existing = list(get_store().existing_bvids())
    fresh_candidates = exclude_existing_candidates(candidates, existing)
    if candidates and not fresh_candidates:
        return {
            "provider": "",
            "items": [],
            "fallbackReason": "all candidates are already in history",
        }
    messages = _discovery_payload(payload, settings, fresh_candidates, limit)
    errors = []

    if deepseek.get("apiKey"):
        try:
            result = deepseek_chat(
                deepseek.get("apiKey"),
                deepseek.get("model") or DEFAULT_DEEPSEEK_MODEL,
                messages,
                max_tokens=1800,
                base_url=deepseek.get("baseUrl"),
            )
            items = filter_discovery_items(result, fresh_candidates, existing, limit=limit)
            return {"provider": "deepseek", "items": items, "fallbackReason": ""}
        except Exception as error:
            errors.append(f"DeepSeek: {error}")

    if qwen.get("apiKey"):
        try:
            result = qwen_chat(
                qwen.get("apiKey"),
                qwen.get("model") or DEFAULT_QWEN_MODEL,
                messages,
                max_tokens=1800,
                base_url=qwen.get("baseUrl"),
            )
            items = filter_discovery_items(result, fresh_candidates, existing, limit=limit)
            return {
                "provider": "qwen",
                "items": items,
                "fallbackReason": "; ".join(errors),
            }
        except Exception as error:
            errors.append(f"千问: {error}")

    if errors:
        raise JobError(
            "AI 视频筛选失败",
            category="api_error",
            suggestion="检查 DeepSeek/千问余额、Key 和模型名称后重试",
        )
    raise JobError(
        "请先配置 DeepSeek 或千问 API Key",
        category="manual_required",
        suggestion="打开系统设置，保存 DeepSeek 或千问 API Key 后再开始视频发现",
    )


def is_browser_closed_error(error):
    text = str(error or "").lower()
    markers = [
        "target page, context or browser has been closed",
        "browser has been closed",
        "target closed",
    ]
    return any(marker in text for marker in markers)


def close_browser_context(context):
    if not context:
        return
    try:
        context.close()
    except Exception as error:
        if not is_browser_closed_error(error):
            raise


def login_window_closed_result(message="登录窗口已关闭，如已完成登录可继续运行工作流"):
    return {"manualRequired": True, "message": message}


def open_login_landing_page(page):
    try:
        page.goto(
            "https://www.bilibili.com/",
            wait_until="domcontentloaded",
            timeout=30000,
        )
        return ""
    except Exception as error:
        if is_browser_closed_error(error):
            raise
        return "登录窗口已经打开，但页面暂时无法自动加载，请在地址栏输入 bilibili.com 后继续登录。"


def action_import_videos(job, payload):
    urls = _payload_urls(payload)
    dry_run = _truthy(payload.get("dryRun"), default=True)
    get_jobs().update(job.id, progress=35, message="正在导入并去重视频链接", event_type="progress")
    result = get_store().import_videos(urls, source="manual", dry_run=dry_run)
    get_jobs().update(job.id, progress=85, message="视频导入预演完成" if dry_run else "视频导入完成", event_type="progress")
    return result


def _clean_keyword_part(value):
    text = str(value or "").replace("\u3000", " ")
    text = re.sub(r"\s+", " ", text).strip()
    text = KEYWORD_PREFIX_RE.sub("", text).strip()
    return text.strip(KEYWORD_EDGE_CHARS)


def normalize_keywords(value, limit=KEYWORD_NORMALIZE_LIMIT):
    try:
        limit = max(1, min(50, int(limit or KEYWORD_NORMALIZE_LIMIT)))
    except Exception:
        limit = KEYWORD_NORMALIZE_LIMIT
    if isinstance(value, (list, tuple, set)):
        raw_items = value
    else:
        raw_items = [value]
    keywords = []
    seen = set()
    for raw in raw_items:
        if isinstance(raw, dict):
            raw = raw.get("keyword") or raw.get("text") or raw.get("name") or ""
        for part in KEYWORD_SPLIT_RE.split(str(raw or "")):
            keyword = _clean_keyword_part(part)
            key = keyword.lower()
            if not keyword or key in seen:
                continue
            seen.add(key)
            keywords.append(keyword)
            if len(keywords) >= limit:
                return keywords
    return keywords


def _payload_keywords(payload):
    return normalize_keywords(payload.get("keywords") or [], limit=KEYWORD_NORMALIZE_LIMIT)


def _discovery_queue_target(payload):
    try:
        return max(1, min(DISCOVERY_MAX_QUEUE_TARGET, int((payload or {}).get("limit") or DISCOVERY_DEFAULT_QUEUE_TARGET)))
    except Exception:
        return DISCOVERY_DEFAULT_QUEUE_TARGET


def _discovery_max_pages(payload):
    try:
        return max(1, min(500, int((payload or {}).get("maxPages") or (payload or {}).get("_maxPages") or DISCOVERY_MAX_PAGES)))
    except Exception:
        return DISCOVERY_MAX_PAGES


def _candidate_bvid(item):
    if not item:
        return ""
    return item.get("bvid") or extract_bvid(item.get("url") or "")


def _candidate_bvid_sequence(candidates):
    return tuple(_candidate_bvid(item) for item in candidates or [] if _candidate_bvid(item))


def _split_fresh_candidates(candidates, existing):
    existing_set = {str(item or "") for item in existing or []}
    fresh = []
    seen_page = set()
    duplicate = 0
    for item in candidates or []:
        bvid = _candidate_bvid(item)
        if not bvid:
            continue
        if bvid in existing_set or bvid in seen_page:
            duplicate += 1
            continue
        seen_page.add(bvid)
        fresh.append(item)
    return fresh, duplicate


def _safe_int(value, default=0):
    try:
        return int(str(value).replace(",", ""))
    except Exception:
        return int(default or 0)


def _local_candidate_score(item, index):
    title = str(item.get("title") or "").lower()
    keyword = str(item.get("keyword") or "").strip().lower()
    keyword_hit = 1 if keyword and keyword in title else 0
    term_hits = sum(1 for term in DISCOVERY_FALLBACK_TERMS if term and term in title)
    page_score = -_safe_int(item.get("search_page"), 9999)
    play_score = _safe_int(item.get("play"), 0)
    return (keyword_hit, term_hits, page_score, play_score, -index)


def _local_fallback_candidates(candidates, limit, exclude=None):
    excluded = {str(item or "") for item in exclude or []}
    ranked = []
    for index, item in enumerate(candidates or []):
        bvid = _candidate_bvid(item)
        if not bvid or bvid in excluded:
            continue
        ranked.append((_local_candidate_score(item, index), item))
    ranked.sort(key=lambda item: item[0], reverse=True)
    return [item for _score, item in ranked[: max(0, int(limit or 0))]]


def _merge_selected_candidate(selected_item, source_by_bvid):
    bvid = _candidate_bvid(selected_item)
    base = source_by_bvid.get(bvid) or {}
    merged = {**base, **(selected_item or {})}
    if bvid:
        merged["bvid"] = bvid
    if not merged.get("url") and bvid:
        merged["url"] = f"https://www.bilibili.com/video/{bvid}"
    return merged


def _select_candidates_with_ai_or_fallback(payload, candidates, needed):
    needed = max(0, int(needed or 0))
    if needed <= 0 or not candidates:
        return {"provider": "", "items": [], "aiSelected": 0, "fallbackSelected": 0, "fallbackReason": ""}

    source_by_bvid = {_candidate_bvid(item): item for item in candidates or [] if _candidate_bvid(item)}
    selected = []
    selected_bvids = set()
    provider = ""
    fallback_reason = ""
    ai_error = ""

    try:
        ai_result = filter_candidates_with_ai(payload, candidates, needed)
        provider = ai_result.get("provider") or ""
        fallback_reason = ai_result.get("fallbackReason") or ""
        for item in ai_result.get("items") or []:
            bvid = _candidate_bvid(item)
            if not bvid or bvid in selected_bvids or bvid not in source_by_bvid:
                continue
            selected_bvids.add(bvid)
            selected.append(_merge_selected_candidate(item, source_by_bvid))
            if len(selected) >= needed:
                break
    except Exception as error:
        ai_error = getattr(error, "message", None) or str(error)
        provider = "fallback"

    ai_selected = len(selected)
    fallback_items = []
    if len(selected) < needed:
        fallback_items = _local_fallback_candidates(candidates, needed - len(selected), exclude=selected_bvids)
        for item in fallback_items:
            bvid = _candidate_bvid(item)
            if not bvid or bvid in selected_bvids:
                continue
            selected_bvids.add(bvid)
            selected.append(item)

    fallback_selected = len(fallback_items)
    reasons = []
    if fallback_reason:
        reasons.append(str(fallback_reason))
    if ai_error:
        reasons.append(ai_error)
    elif fallback_selected:
        reasons.append("AI selected 0; local fallback used" if ai_selected == 0 else "local fallback filled remaining slots")
    if not provider and fallback_selected:
        provider = "fallback"
    return {
        "provider": provider,
        "items": selected[:needed],
        "aiSelected": ai_selected,
        "fallbackSelected": fallback_selected,
        "fallbackReason": "; ".join(reason for reason in reasons if reason),
    }


def _empty_import_result(dry_run):
    return {"total": 0, "inserted": 0, "duplicate": 0, "invalid": 0, "items": [], "dryRun": bool(dry_run)}


def _merge_import_result(total, result):
    total["total"] += int((result or {}).get("total") or 0)
    total["inserted"] += int((result or {}).get("inserted") or 0)
    total["duplicate"] += int((result or {}).get("duplicate") or 0)
    total["invalid"] += int((result or {}).get("invalid") or 0)
    total["items"].extend((result or {}).get("items") or [])


def _deadline_reached(deadline):
    try:
        return deadline is not None and time.monotonic() >= float(deadline)
    except Exception:
        return False


def _discovery_job_update(job_id, message, progress=None):
    if not job_id:
        return
    try:
        changes = {"message": message}
        if progress is not None:
            changes["progress"] = progress
        get_jobs().update(job_id, event_type="progress", **changes)
    except Exception:
        pass


def _mark_keyword_exhausted(state, exhausted_keywords, keyword, reason):
    state[keyword]["exhausted"] = True
    state[keyword]["reason"] = reason
    if keyword not in exhausted_keywords:
        exhausted_keywords.append(keyword)


def build_keyword_normalize_messages(raw_text, local_keywords, limit):
    return [
        {
            "role": "system",
            "content": (
                "你是搜索关键词整理助手。只输出 JSON，格式为 {\"keywords\":[\"...\"]}。"
                "把用户粘贴的内容整理成适合 B站视频搜索的短关键词；去掉编号、项目符号、重复项和说明文字；"
                "不要编造品牌或产品词；最多返回指定数量。"
            ),
        },
        {
            "role": "user",
            "content": json.dumps(
                {
                    "limit": limit,
                    "text": raw_text,
                    "localKeywords": local_keywords,
                },
                ensure_ascii=False,
            ),
        },
    ]


def normalize_keywords_for_payload(payload):
    raw_text = payload.get("text") or payload.get("keywords") or ""
    limit = payload.get("limit") or KEYWORD_NORMALIZE_LIMIT
    local_keywords = normalize_keywords(raw_text, limit=limit)
    response = {
        "ok": True,
        "provider": "local",
        "keywords": local_keywords,
        "text": "\n".join(local_keywords),
    }
    settings = get_store().get_settings(include_secrets=True)
    deepseek = settings.get("deepseek") or {}
    api_key = deepseek.get("apiKey")
    if not api_key or not str(raw_text or "").strip():
        return response
    try:
        normalized = deepseek_chat(
            api_key,
            deepseek.get("model") or DEFAULT_DEEPSEEK_MODEL,
            build_keyword_normalize_messages(str(raw_text), local_keywords, limit),
            max_tokens=400,
            json_mode=True,
            base_url=deepseek.get("baseUrl"),
        )
        ai_keywords = normalize_keywords(normalized.get("keywords") or [], limit=limit)
        if ai_keywords:
            return {
                "ok": True,
                "provider": "deepseek",
                "keywords": ai_keywords,
                "text": "\n".join(ai_keywords),
            }
    except Exception as error:
        response["fallbackReason"] = str(error)
    return response


def discover_videos_for_payload(payload, dry_run):
    payload = dict(payload or {})
    keywords = _payload_keywords(payload)
    if not keywords:
        raise JobError("请至少提供一个搜索关键词", category="api_error", suggestion="填写 keywords 后重试")

    queue_target = _discovery_queue_target(payload)
    pending_before = int(get_store().summary().get("queue_pending") or 0)
    needed = max(0, queue_target - pending_before)
    import_total = _empty_import_result(dry_run)
    stats = {
        "queueTarget": queue_target,
        "pendingBefore": pending_before,
        "needed": needed,
        "searchedPages": 0,
        "freshCandidates": 0,
        "duplicateCandidates": 0,
        "aiSelected": 0,
        "fallbackSelected": 0,
        "inserted": 0,
        "exhaustedKeywords": [],
        "stoppedReason": "",
    }
    if needed <= 0:
        return {
            "candidates": 0,
            "provider": "",
            "fallbackReason": "",
            "selected": [],
            **stats,
            **import_total,
            "stoppedReason": "queue_full",
        }

    year = payload.get("year") or ""
    job_id = payload.get("_jobId") or payload.get("_job_id")
    deadline = payload.get("_deadline")
    max_pages = _discovery_max_pages(payload)
    page_size = max(DISCOVERY_PAGE_SIZE, min(50, queue_target * 2))
    pending_virtual = pending_before
    existing = set(get_store().existing_bvids())
    seen_this_run = set()
    selected_items = []
    provider = ""
    fallback_reasons = []
    keyword_state = {keyword: {"last_signature": None, "exhausted": False, "reason": ""} for keyword in keywords}

    _discovery_job_update(job_id, f"队列 {pending_before}/{queue_target}，正在补 {needed} 个新视频", progress=22)

    for page in range(1, max_pages + 1):
        active_keywords = [keyword for keyword in keywords if not keyword_state[keyword]["exhausted"]]
        if not active_keywords:
            stats["stoppedReason"] = "all_keywords_exhausted"
            break

        for keyword in active_keywords:
            if pending_virtual >= queue_target:
                stats["stoppedReason"] = "queue_full"
                break
            if job_id and _job_stop_requested(job_id):
                stats["stoppedReason"] = "stopped"
                break
            if _deadline_reached(deadline):
                stats["stoppedReason"] = "deadline"
                break

            page_items = []
            page_error = None
            try:
                page_items = search_bili_videos_page(keyword, page=page, limit=page_size, year=year)
            except HTTPError as error:
                page_error = error
                try:
                    page_items = search_bili_videos_with_browser_accounts(
                        [keyword],
                        per_keyword=page_size,
                        year=year,
                        max_pages=2,
                    )
                except Exception as browser_error:
                    fallback_reasons.append(f"{keyword} page {page}: {browser_error}")
                    page_items = []
            except Exception as error:
                page_error = error

            stats["searchedPages"] += 1
            if page_error and not page_items:
                fallback_reasons.append(f"{keyword} page {page}: {getattr(page_error, 'reason', None) or page_error}")
                _mark_keyword_exhausted(keyword_state, stats["exhaustedKeywords"], keyword, "search_error")
                _discovery_job_update(job_id, f"{keyword} 第 {page} 页：接口受限或失败，已切换关键词", progress=28)
                continue

            signature = _candidate_bvid_sequence(page_items)
            if not signature:
                _mark_keyword_exhausted(keyword_state, stats["exhaustedKeywords"], keyword, "empty_page")
                _discovery_job_update(job_id, f"{keyword} 第 {page} 页：本页 0 条，新 BV 0 条，重复 0 条", progress=28)
                continue
            if keyword_state[keyword]["last_signature"] == signature:
                _mark_keyword_exhausted(keyword_state, stats["exhaustedKeywords"], keyword, "repeating_page")
                _discovery_job_update(job_id, f"{keyword} 第 {page} 页：分页结果重复，已标记耗尽", progress=28)
                continue
            keyword_state[keyword]["last_signature"] = signature

            fresh, duplicate_count = _split_fresh_candidates(page_items, existing | seen_this_run)
            for item in fresh:
                bvid = _candidate_bvid(item)
                if bvid:
                    seen_this_run.add(bvid)
            stats["freshCandidates"] += len(fresh)
            stats["duplicateCandidates"] += duplicate_count
            _discovery_job_update(
                job_id,
                f"{keyword} 第 {page} 页：本页 {len(page_items)} 条，新 BV {len(fresh)} 条，重复 {duplicate_count} 条",
                progress=30,
            )

            if not fresh:
                continue

            remaining = max(0, queue_target - pending_virtual)
            selection = _select_candidates_with_ai_or_fallback(payload, fresh, remaining)
            if selection.get("provider"):
                provider = selection.get("provider")
            if selection.get("fallbackReason"):
                fallback_reasons.append(selection.get("fallbackReason"))
            stats["aiSelected"] += int(selection.get("aiSelected") or 0)
            stats["fallbackSelected"] += int(selection.get("fallbackSelected") or 0)
            selected_page_items = selection.get("items") or []
            urls = [item.get("url") for item in selected_page_items if item.get("url")]
            if not urls:
                continue

            import_result = get_store().import_videos(urls, source="search", dry_run=dry_run)
            _merge_import_result(import_total, import_result)
            pending_virtual += int(import_result.get("inserted") or 0)
            stats["inserted"] = import_total["inserted"]
            for item in selected_page_items:
                bvid = _candidate_bvid(item)
                if bvid:
                    existing.add(bvid)
            selected_items.extend(selected_page_items)
            _discovery_job_update(
                job_id,
                (
                    f"AI 选中 {selection.get('aiSelected') or 0} 条，兜底补 {selection.get('fallbackSelected') or 0} 条，"
                    f"当前队列 {pending_virtual}/{queue_target}"
                ),
                progress=45,
            )

            if pending_virtual >= queue_target:
                stats["stoppedReason"] = "queue_full"
                break

        if stats["stoppedReason"]:
            break
    else:
        if pending_virtual >= queue_target:
            stats["stoppedReason"] = "queue_full"
        elif all(keyword_state[keyword]["exhausted"] for keyword in keywords):
            stats["stoppedReason"] = "all_keywords_exhausted"
        else:
            stats["stoppedReason"] = "page_limit"

    if not stats["stoppedReason"]:
        if pending_virtual >= queue_target:
            stats["stoppedReason"] = "queue_full"
        elif all(keyword_state[keyword]["exhausted"] for keyword in keywords):
            stats["stoppedReason"] = "all_keywords_exhausted"
        else:
            stats["stoppedReason"] = "page_limit"

    if stats["stoppedReason"] == "all_keywords_exhausted":
        _discovery_job_update(job_id, f"所有关键词已无更多新视频，当前队列 {pending_virtual}/{queue_target}，等待下一轮", progress=65)

    fallback_reason = "; ".join(dict.fromkeys(reason for reason in fallback_reasons if reason))
    return {
        "candidates": stats["freshCandidates"] + stats["duplicateCandidates"],
        "provider": provider,
        "fallbackReason": fallback_reason,
        "selected": selected_items,
        "pendingAfter": pending_virtual,
        **stats,
        **import_total,
    }


def discover_videos_for_payload_legacy(payload, dry_run):
    keywords = _payload_keywords(payload)
    if not keywords:
        raise JobError("请至少提供一个搜索关键词", category="api_error", suggestion="填写 keywords 后重试")
    try:
        limit = max(1, min(50, int(payload.get("limit") or 10)))
    except Exception:
        limit = 10
    year = payload.get("year") or ""
    search_limit = max(8, limit * 2)
    try:
        candidates = search_bili_videos(keywords, per_keyword=search_limit, year=year, max_pages=5)
    except JobError:
        raise
    except HTTPError as error:
        candidates = search_bili_videos_with_browser_accounts(keywords, per_keyword=search_limit, year=year, max_pages=2)
        if not candidates:
            raise bili_http_error_to_job_error(error, action="搜索") from error
    except Exception as error:
        raise JobError(str(error), category="api_error", suggestion="检查网络或 B站接口后重试") from error
    if not candidates:
        candidates = search_bili_videos_with_browser_accounts(keywords, per_keyword=search_limit, year=year, max_pages=2)
    ai_result = filter_candidates_with_ai(payload, candidates, limit) if candidates else {"provider": "", "items": []}
    selected = ai_result.get("items") or []
    urls = [item.get("url") for item in selected[:limit] if item.get("url")]
    import_result = get_store().import_videos(urls, source="search", dry_run=dry_run)
    return {
        "candidates": len(candidates),
        "provider": ai_result.get("provider", ""),
        "fallbackReason": ai_result.get("fallbackReason", ""),
        "selected": selected,
        **import_result,
    }


def action_search_videos(job, payload):
    dry_run = _truthy(payload.get("dryRun"), default=True)
    get_jobs().update(job.id, progress=20, message="正在搜索 B站公开视频", event_type="progress")
    result = discover_videos_for_payload({**(payload or {}), "_jobId": job.id}, dry_run)
    get_jobs().update(job.id, progress=90, message="视频发现完成" if not dry_run else "发布计划已生成", event_type="progress")
    return result


def _int_payload(payload, key, default=0, minimum=0, maximum=None):
    try:
        value = int(payload.get(key) if payload.get(key) not in [None, ""] else default)
    except Exception:
        value = int(default or 0)
    value = max(int(minimum), value)
    if maximum is not None:
        value = min(int(maximum), value)
    return value


def _minutes_payload_to_seconds(payload, key, default_seconds, minimum_minutes=0, maximum_minutes=24 * 60):
    if payload.get(key) in [None, ""]:
        return int(default_seconds or 0)
    return _int_payload(payload, key, default=default_seconds // 60, minimum=minimum_minutes, maximum=maximum_minutes) * 60


def _seconds_payload(payload, seconds_key, minutes_key, default_seconds, maximum_seconds=24 * 60 * 60):
    if payload.get(seconds_key) not in [None, ""]:
        return _int_payload(payload, seconds_key, default=default_seconds, minimum=0, maximum=maximum_seconds)
    return _minutes_payload_to_seconds(payload, minutes_key, default_seconds)


def _job_stop_requested(job_id):
    current = get_jobs().get(job_id)
    return bool(current and current.stop_requested)


def _wait_with_stop(job_id, seconds, message, progress=None):
    seconds = max(0, int(seconds or 0))
    if seconds <= 0:
        return not _job_stop_requested(job_id)
    for remaining in range(seconds, 0, -1):
        if _job_stop_requested(job_id):
            return False
        if remaining == seconds or remaining % 10 == 0:
            changes = {"message": f"{message}，剩余约 {remaining} 秒"}
            if progress is not None:
                changes["progress"] = progress
            get_jobs().update(job_id, event_type="progress", **changes)
        time.sleep(1)
    return not _job_stop_requested(job_id)


def should_wait_after_comment_attempt(result):
    result = result or {}
    return int(result.get("success") or 0) > 0 or int(result.get("pendingConfirmation") or 0) > 0


def cap_wait_to_deadline(seconds, deadline=None, now=None):
    seconds = max(0, int(seconds or 0))
    if deadline is None:
        return seconds
    current = time.monotonic() if now is None else float(now)
    remaining = max(0, int(float(deadline) - current))
    return min(seconds, remaining)


def action_run_integrated_workflow(job, payload):
    dry_run = _truthy(payload.get("dryRun"), default=True)
    duration_seconds = _seconds_payload(payload, "durationSeconds", "durationMinutes", 0)
    cycle_interval_seconds = _seconds_payload(payload, "cycleIntervalSeconds", "cycleIntervalMinutes", 30 * 60)
    duration_minutes = duration_seconds / 60
    cycle_interval_minutes = cycle_interval_seconds / 60
    max_count = _int_payload(payload, "maxCount", default=1, minimum=1, maximum=50)
    library_id = library_id_from_payload(payload)
    if not str(library_id or "").strip():
        raise JobError("请选择评论词库", category="manual_required", suggestion="本轮运行必须选择一个评论词库")
    if duration_seconds > 0 and cycle_interval_seconds <= 0:
        cycle_interval_seconds = 60
        cycle_interval_minutes = 1
    deadline = time.monotonic() + duration_seconds if duration_seconds > 0 else None
    config = load_app_config()
    scheduler = config.get("scheduler", {})
    account_interval_min = _seconds_payload(
        payload,
        "accountIntervalMinSeconds",
        "accountIntervalMinMinutes",
        int(scheduler.get("account_interval_min_seconds", 480)),
    )
    account_interval_max = _seconds_payload(
        payload,
        "accountIntervalMaxSeconds",
        "accountIntervalMaxMinutes",
        int(scheduler.get("account_interval_max_seconds", 900)),
    )
    account_interval_max = max(account_interval_min, account_interval_max)
    same_account_cooldown_min = _seconds_payload(
        payload,
        "sameAccountCooldownMinSeconds",
        "sameAccountCooldownMinMinutes",
        int(scheduler.get("same_account_cooldown_min_seconds", 1200)),
    )
    same_account_cooldown_max = _seconds_payload(
        payload,
        "sameAccountCooldownMaxSeconds",
        "sameAccountCooldownMaxMinutes",
        int(scheduler.get("same_account_cooldown_max_seconds", 2400)),
    )
    same_account_cooldown_max = max(same_account_cooldown_min, same_account_cooldown_max)

    manual_urls = _payload_urls({"urls": payload.get("manualUrls") or []})
    manual_import = _empty_import_result(dry_run)
    if manual_urls:
        get_jobs().update(job.id, progress=3, message="正在导入指定视频并去重", event_type="progress")
        manual_import = get_store().import_videos(manual_urls, source="manual", dry_run=dry_run)

    totals = {
        "dryRun": dry_run,
        "cycles": 0,
        "searched": 0,
        "inserted": int(manual_import.get("inserted") or 0),
        "duplicate": int(manual_import.get("duplicate") or 0),
        "invalid": int(manual_import.get("invalid") or 0),
        "success": 0,
        "pendingConfirmation": 0,
        "failed": 0,
        "published": 0,
        "items": [],
        "cycleResults": [],
        "durationMinutes": duration_minutes,
        "cycleIntervalMinutes": cycle_interval_minutes,
        "durationSeconds": duration_seconds,
        "cycleIntervalSeconds": cycle_interval_seconds,
        "accountIntervalSeconds": [account_interval_min, account_interval_max],
        "sameAccountCooldownSeconds": [same_account_cooldown_min, same_account_cooldown_max],
        "manualImport": manual_import,
    }

    while True:
        if _job_stop_requested(job.id):
            break
        if deadline and time.monotonic() >= deadline:
            break

        cycle_number = totals["cycles"] + 1
        progress = 5 if not deadline else min(95, max(5, int((1 - max(0, deadline - time.monotonic()) / duration_seconds) * 95)))
        get_jobs().update(
            job.id,
            progress=progress,
            message=f"第 {cycle_number} 轮：正在搜索视频并去重入队",
            event_type="progress",
        )
        try:
            discovery = discover_videos_for_payload(
                {**payload, "dryRun": dry_run, "_jobId": job.id, "_deadline": deadline},
                dry_run,
            )
        except JobError as error:
            pending_count = int(get_store().summary().get("queue_pending") or 0)
            if dry_run:
                raise
            if pending_count <= 0 and not deadline:
                raise
            if pending_count <= 0:
                discovery = {
                    "candidates": 0,
                    "provider": "",
                    "fallbackReason": error.message,
                    "selected": [],
                    "total": 0,
                    "inserted": 0,
                    "duplicate": 0,
                    "invalid": 0,
                    "items": [],
                    "searchError": error.message,
                    "retryNextCycle": True,
                }
                get_jobs().update(
                    job.id,
                    progress=8,
                    message=f"本轮搜索暂时被拒绝，队列为空，将等待下一轮重新搜索：{error.message}",
                    event_type="progress",
                )
            else:
                discovery = {
                    "candidates": 0,
                    "provider": "",
                    "fallbackReason": error.message,
                    "selected": [],
                    "total": 0,
                    "inserted": 0,
                    "duplicate": 0,
                    "invalid": 0,
                    "items": [],
                    "searchError": error.message,
                    "continuedWithPendingQueue": True,
                    "pendingQueue": pending_count,
                }
                get_jobs().update(
                    job.id,
                    progress=8,
                    message=f"搜索暂时失败，已改用现有待处理队列继续发布：{error.message}",
                    event_type="progress",
                )
        cycle_result = {
            "cycle": cycle_number,
            "discovery": discovery,
            "plan": None,
            "success": 0,
            "failed": 0,
            "published": 0,
        }
        totals["cycles"] = cycle_number
        totals["searched"] += int(discovery.get("total") or 0)
        totals["inserted"] += int(discovery.get("inserted") or 0)
        totals["duplicate"] += int(discovery.get("duplicate") or 0)
        totals["invalid"] += int(discovery.get("invalid") or 0)

        if dry_run:
            plan = get_store().build_comment_plan(
                max_count=max_count,
                account_ids=payload.get("accountIds") or [],
                library_id=library_id,
            )
            cycle_result["plan"] = plan
            totals["items"].extend(plan.get("items") or [])
        else:
            remaining_publish_count = max(0, max_count - totals["published"])
            for publish_index in range(remaining_publish_count):
                if _job_stop_requested(job.id) or (deadline and time.monotonic() >= deadline):
                    break
                get_jobs().update(
                    job.id,
                    progress=min(95, progress + 1),
                    message=f"第 {cycle_number} 轮：正在选择可用账号并发布第 {publish_index + 1} 条",
                    event_type="progress",
                )
                try:
                    publish_result = action_run_comment_workflow(
                        job,
                        {
                            **payload,
                            "dryRun": False,
                            "maxCount": 1,
                            "accountIntervalMinSeconds": account_interval_min,
                            "accountIntervalMaxSeconds": account_interval_max,
                            "sameAccountCooldownMinSeconds": same_account_cooldown_min,
                            "sameAccountCooldownMaxSeconds": same_account_cooldown_max,
                            "_deadline": deadline,
                        },
                    )
                except JobError as error:
                    if error.category == "manual_required" and "可用账号" in error.message:
                        next_available = get_store().summary().get("next_available_at") or ""
                        wait_for_account = seconds_until(next_available)
                        cycle_result["note"] = "暂无可用账号，等待最近账号冷却结束后继续。"
                        cycle_result["waitSeconds"] = wait_for_account
                        break
                    raise
                cycle_result["plan"] = publish_result
                if not publish_result.get("total"):
                    break
                success = int(publish_result.get("success") or 0)
                pending_confirmation = int(publish_result.get("pendingConfirmation") or 0)
                failed = int(publish_result.get("failed") or 0)
                cycle_result["success"] += success
                cycle_result["pendingConfirmation"] = int(cycle_result.get("pendingConfirmation") or 0) + pending_confirmation
                cycle_result["failed"] += failed
                cycle_result["published"] += success + pending_confirmation
                totals["success"] += success
                totals["pendingConfirmation"] += pending_confirmation
                totals["failed"] += failed
                totals["published"] += success + pending_confirmation
                totals["items"].extend(publish_result.get("items") or [])
                if publish_index < remaining_publish_count - 1 and should_wait_after_comment_attempt(publish_result):
                    wait_seconds = random.randint(account_interval_min, max(account_interval_min, account_interval_max))
                    wait_seconds = cap_wait_to_deadline(wait_seconds, deadline)
                    if wait_seconds <= 0:
                        break
                    if not _wait_with_stop(
                        job.id,
                        wait_seconds,
                        f"账号切换等待中（本次随机 {wait_seconds} 秒）",
                        progress=min(95, progress + 2),
                    ):
                        break

        totals["cycleResults"].append(cycle_result)
        if totals["published"] >= max_count:
            get_jobs().update(
                job.id,
                progress=95,
                message=f"本次已完成 {totals['published']} 条发布，达到设置上限",
                event_type="progress",
            )
            break
        if not deadline or _job_stop_requested(job.id) or time.monotonic() >= deadline:
            break
        wait_seconds = int(cycle_result.get("waitSeconds") or cycle_interval_seconds)
        if cycle_interval_seconds > 0 and wait_seconds > 0:
            wait_seconds = min(wait_seconds, cycle_interval_seconds)
        wait_seconds = cap_wait_to_deadline(wait_seconds, deadline)
        if wait_seconds <= 0:
            break
        if not _wait_with_stop(job.id, wait_seconds, "下一轮搜索发布等待中", progress=min(95, progress + 3)):
            break

    get_jobs().update(job.id, progress=95, message="一体化流程收尾中", event_type="progress")
    return totals


def action_run_comment_workflow(job, payload):
    dry_run = _truthy(payload.get("dryRun"), default=True)
    try:
        max_count = max(0, int(payload.get("maxCount") or 0))
    except Exception:
        max_count = 0
    account_ids = payload.get("accountIds") or []
    if isinstance(account_ids, str):
        account_ids = [item.strip() for item in account_ids.split(",") if item.strip()]
    library_id = library_id_from_payload(payload)
    if not str(library_id or "").strip():
        raise JobError("请选择评论词库", category="manual_required", suggestion="本轮运行必须选择一个评论词库")

    plan = get_store().build_comment_plan(max_count=max_count, account_ids=account_ids, library_id=library_id)
    missing = plan.get("missing") or {}
    if missing.get("accounts"):
        raise JobError("没有可用账号", category="manual_required", suggestion="请先启用账号并完成登录")
    if missing.get("library"):
        raise JobError("请选择评论词库", category="manual_required", suggestion="本轮运行必须选择一个有效的评论词库")
    if missing.get("templates"):
        raise JobError("没有可用模板", category="api_error", suggestion="请先维护所选评论词库，并启用至少一条评论")
    if missing.get("videos"):
        return {"total": 0, "success": 0, "failed": 0, "items": [], "dryRun": dry_run}

    if dry_run:
        get_jobs().update(job.id, progress=90, message="评论工作流预演完成", event_type="progress")
        return {"dryRun": True, **plan}

    config = load_app_config()
    scheduler = config.get("scheduler", {})
    account_interval_min = int(payload.get("accountIntervalMinSeconds") or scheduler.get("account_interval_min_seconds", 480))
    account_interval_max = int(payload.get("accountIntervalMaxSeconds") or scheduler.get("account_interval_max_seconds", 900))
    account_interval_max = max(account_interval_min, account_interval_max)
    same_account_cooldown_min = int(payload.get("sameAccountCooldownMinSeconds") or scheduler.get("same_account_cooldown_min_seconds", 1200))
    same_account_cooldown_max = int(payload.get("sameAccountCooldownMaxSeconds") or scheduler.get("same_account_cooldown_max_seconds", 2400))
    same_account_cooldown_max = max(same_account_cooldown_min, same_account_cooldown_max)
    operator = BiliOperator(config.get("browser", {}), config.get("comment", {}))
    success = 0
    pending_confirmation = 0
    failed = 0
    results = []
    deadline = payload.get("_deadline")
    total = max(1, len(plan["items"]))
    for index, item in enumerate(plan["items"], start=1):
        if get_jobs().get(job.id).stop_requested:
            break
        progress = int((index - 1) / total * 90)
        get_jobs().update(
            job.id,
            progress=max(5, progress),
            message=f"正在用账号 {item['account_name']} 处理 {item['bvid']}",
            event_type="progress",
        )
        db_account = next(
            (
                acc
                for acc in get_store().list_accounts(enabled_only=True)
                if acc["id"] == item["account_id"]
            ),
            None,
        )
        if not db_account:
            error_message = "账号已停用或删除，本条已留在发现池等待其他账号处理"
            get_store().record_publish_result(item, "account_unavailable", error_message)
            failed += 1
            results.append({**item, "status": "account_unavailable", "error_reason": error_message})
            get_jobs().update(job.id, message=error_message, event_type="warning")
            continue
        if str(db_account.get("login_status") or "").strip().lower() not in {"ok", "logged_in", "success"}:
            error_message = "账号登录状态不可用，本条已留在发现池等待其他账号处理"
            get_store().record_publish_result(item, STATUS_LOGIN_REQUIRED, error_message)
            failed += 1
            results.append({**item, "status": STATUS_LOGIN_REQUIRED, "error_reason": error_message})
            get_jobs().update(job.id, message=error_message, event_type="warning")
            continue
        account = SimpleNamespace(
            name=item["account_name"],
            user_data_dir=db_account["user_data_dir"],
            proxy=db_account.get("proxy") or "",
        )
        # AI 模式评论实时生成与降级闭环
        is_ai = str(item.get("library_id") or "").startswith("__AI__") or str(item.get("library_id") or "").lower() in ["deepseek", "qwen"]
        template_text = item["template_text"]
        if is_ai:
            provider = "deepseek" if "qwen" not in str(item.get("library_id") or "").lower() else "qwen"
            rules = payload.get("rules") or "请生成一条字数少于30字的走心评论。"
            video_info = get_store().get_video(item["bvid"])
            video_title = video_info.get("title") if video_info else ""

            get_jobs().update(
                job.id,
                message=f"正在使用 {provider.upper()} 智能分析视频并生成评论...",
                event_type="progress",
            )

            try:
                settings = get_store().get_settings(include_secrets=True)
                ai_comment = generate_ai_comment(
                    provider,
                    video_title,
                    rules,
                    settings=settings,
                )

                if ai_comment and ai_comment.strip():
                    template_text = ai_comment.strip()
                    # 更新 plan_item 的文本以记录真实台账
                    item["template_text"] = template_text
            except Exception as ai_err:
                template_text = fallback_comment_from_library(payload.get("fallbackLibraryId"))
                if not template_text:
                    error_message = "智能评论生成失败且没有可用回退词库，本条已跳过"
                    item["template_text"] = ""
                    get_store().record_publish_result(item, "skipped", f"{error_message}: {ai_err}")
                    failed += 1
                    results.append({**item, "status": "skipped", "error_reason": error_message})
                    get_jobs().update(job.id, message=error_message, event_type="warning")
                    continue
                item["template_text"] = template_text
                try:
                    get_store().record_log("ai_error", f"智能评论生成失败，已改用回退词库。错误: {str(ai_err)}", bvid=item['bvid'])
                except Exception:
                    pass

        submitted_to_platform = False
        try:
            result = operator.publish_comment(
                account,
                item["url"],
                template_text,
            )
        except Exception as error:
            error_message = f"评论页面执行异常：{error}"
            get_store().record_publish_result(item, "retryable_error", error_message)
            failed += 1
            results.append({**item, "status": "retryable_error", "error_reason": error_message})
            get_jobs().update(job.id, message=error_message, event_type="warning")
            continue
        if result.status == STATUS_SUCCESS:
            success += 1
            submitted_to_platform = True
            get_store().record_publish_result(item, "success", evidence=result.evidence())
            results.append({**item, "status": "success", **result.evidence()})
        elif result.status == STATUS_SUBMITTED_UNVERIFIED:
            pending_confirmation += 1
            submitted_to_platform = True
            get_store().record_publish_result(
                item,
                STATUS_SUBMITTED_UNVERIFIED,
                result.error_reason,
                evidence=result.evidence(),
            )
            results.append({
                **item,
                "status": STATUS_SUBMITTED_UNVERIFIED,
                "error_reason": result.error_reason,
                **result.evidence(),
            })
            get_jobs().update(
                job.id,
                message=f"{item['bvid']} 已提交，等待人工核验",
                event_type="warning",
            )
        else:
            failed += 1
            get_store().record_publish_result(item, result.status, result.error_reason)
            results.append({**item, "status": result.status, "error_reason": result.error_reason})
            if result.status in {STATUS_LOGIN_REQUIRED, STATUS_VERIFY}:
                get_jobs().update(
                    job.id,
                    message=f"账号 {item['account_name']} 需要重新登录或完成验证，已继续处理其他账号",
                    event_type="warning",
                )
                continue
        if submitted_to_platform:
            get_store().mark_account_cooldown(
                item["account_id"],
                same_account_cooldown_min,
                same_account_cooldown_max,
            )
            if index < len(plan["items"]):
                wait_seconds = random.randint(account_interval_min, account_interval_max)
                wait_seconds = cap_wait_to_deadline(wait_seconds, deadline)
                if wait_seconds > 0 and not _wait_with_stop(
                    job.id,
                    wait_seconds,
                    f"账号切换等待中（本次随机 {wait_seconds} 秒）",
                    progress=int(index / total * 90),
                ):
                    break
    return {
        "dryRun": False,
        "total": len(plan["items"]),
        "success": success,
        "pendingConfirmation": pending_confirmation,
        "failed": failed,
        "items": results,
    }


def read_bili_profile(page, cookies):
    cookie_map = {str(item.get("name") or ""): str(item.get("value") or "") for item in (cookies or [])}
    profile = {
        "uid": cookie_map.get("DedeUserID", ""),
        "nickname": "",
        "avatarUrl": "",
    }
    try:
        payload = page.evaluate(
            """async () => {
                const response = await fetch('https://api.bilibili.com/x/web-interface/nav', { credentials: 'include' });
                return await response.json();
            }"""
        )
        data = payload.get("data") if isinstance(payload, dict) else None
        if isinstance(data, dict) and data.get("isLogin"):
            profile["uid"] = str(data.get("mid") or profile["uid"])
            profile["nickname"] = str(data.get("uname") or "").strip()
            profile["avatarUrl"] = str(data.get("face") or "").strip()
    except Exception:
        pass
    return profile


def is_authenticated_bili_session(cookies):
    cookie_map = {
        str(item.get("name") or ""): str(item.get("value") or "").strip()
        for item in (cookies or [])
        if isinstance(item, dict)
    }
    uid = cookie_map.get("DedeUserID", "")
    return bool(
        uid.isdigit()
        and cookie_map.get("SESSDATA")
        and cookie_map.get("bili_jct")
    )


def mark_account_login_required(account_id, note="尚未完成登录"):
    get_store().update_account_login_status(account_id, "login_required", note)
    return get_store().update_account(account_id, {"cookie": ""})


def capture_authenticated_login_session(account, context, page):
    cookies = context.cookies("https://www.bilibili.com") or []
    if not is_authenticated_bili_session(cookies):
        return None

    cookie_str = "; ".join(f"{item['name']}={item['value']}" for item in cookies)
    profile = read_bili_profile(page, cookies)
    try:
        get_store().update_account_login_status(account["id"], "ok", "登录成功")
        updated = get_store().update_account(
            account["id"],
            {
                "cookie": cookie_str,
                "uid": profile.get("uid") or "",
                "nickname": profile.get("nickname") or "",
                "avatarUrl": profile.get("avatarUrl") or "",
            },
        )
    except ValueError as error:
        raise JobError(
            str(error),
            category="manual_required",
            suggestion="该 B 站账号已经托管，请直接使用现有账号",
        ) from error

    return {
        "account": updated,
        "loginStatus": "ok",
        "message": "登录成功，账号会话已同步",
        "cookie": cookie_str,
    }


def check_bili_login_status(account, browser_config):
    from playwright.sync_api import sync_playwright

    context = None
    with AccountDirectoryLock(account["user_data_dir"]):
        with sync_playwright() as playwright:
            try:
                context = launch_persistent_browser_context(
                    playwright,
                    account["user_data_dir"],
                    browser_config,
                    headless=True,
                )
                page = context.pages[0] if context.pages else context.new_page()
                page.goto("https://www.bilibili.com/", wait_until="domcontentloaded", timeout=30000)
                page.wait_for_timeout(1200)
                try:
                    body_text = page.locator("body").inner_text(timeout=3000)
                except Exception:
                    body_text = page.content()
                if any(keyword in body_text for keyword in VERIFY_KEYWORDS):
                    return {"status": "verify_required", "message": "需要完成验证码或安全验证"}
                if any(keyword in body_text for keyword in LOGIN_KEYWORDS) or "立即登录" in body_text:
                    return {"status": "login_required", "message": "登录已失效或需要重新登录"}

                cookies = context.cookies("https://www.bilibili.com") or []
                cookie_str = "; ".join(f"{c['name']}={c['value']}" for c in cookies)
                profile = read_bili_profile(page, cookies)
                if not is_authenticated_bili_session(cookies):
                    return {"status": "login_required", "message": "尚未完成登录"}
                return {
                    "status": "ok",
                    "message": "登录状态有效",
                    "cookie": cookie_str,
                    **profile,
                }
            except Exception as error:
                if is_browser_closed_error(error):
                    return {"status": "unknown", "message": "检查窗口被关闭，无法确认登录状态"}
                raise
            finally:
                close_browser_context(context)


def action_check_account_login(job, payload):
    account_id = payload.get("accountId") or payload.get("account_id")
    account = None
    for candidate in get_store().list_accounts():
        if str(candidate["id"]) == str(account_id) or candidate["name"] == str(account_id):
            account = candidate
            break
    if not account:
        raise JobError("未找到账号", category="api_error", suggestion="请检查 accountId")
    get_jobs().update(job.id, progress=10, message=f"正在检查账号 {account['name']} 的登录状态", event_type="progress")
    result = check_bili_login_status(account, load_app_config().get("browser", {}))
    if result.get("status") == "ok":
        updated = get_store().update_account_login_status(account["id"], "ok", result.get("message"))
        updated = get_store().update_account(account["id"], {
            "cookie": result.get("cookie") or "",
            "uid": result.get("uid") or "",
            "nickname": result.get("nickname") or "",
            "avatarUrl": result.get("avatarUrl") or "",
        })
    else:
        updated = mark_account_login_required(account["id"], result.get("message") or "尚未完成登录")
    get_jobs().update(job.id, progress=90, message=result.get("message") or "登录状态检查完成", event_type="progress")
    return {
        "account": updated,
        "loginStatus": updated.get("login_status"),
        "message": updated.get("login_note") or result.get("message") or "",
        "cookie": result.get("cookie") or ""
    }


def action_login_account(job, payload):
    account_id = payload.get("accountId") or payload.get("account_id")
    accounts = get_store().list_accounts()
    account = None
    for candidate in accounts:
        if str(candidate["id"]) == str(account_id) or candidate["name"] == str(account_id):
            account = candidate
            break
    if not account:
        raise JobError("未找到账号", category="api_error", suggestion="请检查 accountId")
    config = load_app_config()
    get_jobs().update(job.id, progress=10, message=f"正在打开账号 {account['name']} 的登录窗口", event_type="progress")
    from playwright.sync_api import sync_playwright

    with AccountDirectoryLock(account["user_data_dir"]):
        with sync_playwright() as playwright:
            context = None
            try:
                context = launch_persistent_browser_context(
                    playwright,
                    account["user_data_dir"],
                    config.get("browser", {}),
                    headless=False,
                )
                page = context.pages[0] if context.pages else context.new_page()
                navigation_warning = open_login_landing_page(page)
                if navigation_warning:
                    get_jobs().update(
                        job.id,
                        progress=20,
                        message=navigation_warning,
                        event_type="warning",
                    )
                deadline = time.monotonic() + int(payload.get("timeoutSeconds") or 300)
                while time.monotonic() < deadline:
                    current_job = get_jobs().get(job.id)
                    if current_job and current_job.stop_requested:
                        close_browser_context(context)
                        return {"stopped": True, "message": "登录浏览器已关闭"}
                    authenticated = capture_authenticated_login_session(account, context, page)
                    if authenticated:
                        get_jobs().update(
                            job.id,
                            progress=95,
                            message="登录成功，正在关闭登录窗口",
                            event_type="progress",
                        )
                        return authenticated
                    try:
                        page.wait_for_timeout(1000)
                    except Exception as error:
                        if is_browser_closed_error(error):
                            return login_window_closed_result()
                        raise
                return login_window_closed_result()
            except Exception as error:
                if is_browser_closed_error(error):
                    return login_window_closed_result()
                raise
            finally:
                close_browser_context(context)


class Handler(SimpleHTTPRequestHandler):
    server_version = "BiliFeedbackLocal/1.0"

    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=BUNDLE_DIR, **kwargs)

    def log_message(self, format, *args):
        if os.environ.get("BILI_WORKBENCH_HTTP_LOG") == "1":
            super().log_message(format, *args)

    def end_headers(self):
        self.send_header("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0")
        self.send_header("Pragma", "no-cache")
        self.send_header("Expires", "0")
        super().end_headers()

    def send_json(self, payload, status=200):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def send_sse(self, text):
        body = text.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def structured_error(self, message, category="unknown", recoverable=True, suggestion="查看日志后重试"):
        return {
            "ok": False,
            "status": "error",
            "error": message,
            "category": category,
            "recoverable": recoverable,
            "suggestion": suggestion,
        }

    def start_action(self, action_id, worker):
        payload = {}
        try:
            payload = read_json_body(self)
            job = get_jobs().start(action_id, worker, payload)
            self.send_json({"jobId": job.id, "status": "started", "message": "任务已启动"})
        except JobError as error:
            self.send_json(
                self.structured_error(error.message, error.category, error.recoverable, error.suggestion),
                status=200,
            )
        except Exception as error:
            self.send_json(self.structured_error(str(error)), status=200)

    def do_GET(self):
        parsed = urlparse(self.path)
        if parsed.path == "/api/health":
            self.send_json({"ok": True, "status": "healthy", "time": app_now(), "version": APP_VERSION})
            return

        if parsed.path == "/api/state/summary":
            self.send_json({"ok": True, "summary": get_store().summary()})
            return

        if parsed.path == "/api/settings":
            self.send_json({"ok": True, "settings": get_store().get_settings()})
            return

        if parsed.path == "/api/settings/ai":
            self.send_json({"ok": True, "settings": get_store().get_settings()})
            return

        if parsed.path == "/api/accounts":
            self.send_json({"ok": True, "accounts": get_store().list_accounts()})
            return

        if parsed.path.startswith("/api/accounts/") and parsed.path.endswith("/cookie"):
            try:
                parts = parsed.path.strip("/").split("/")
                account_id = parts[2] if len(parts) >= 4 else ""
                account = None
                for candidate in get_store().list_accounts():
                    if str(candidate["id"]) == str(account_id) or candidate["name"] == str(account_id):
                        account = candidate
                        break
                if not account:
                    self.send_json({"ok": False, "error": "未找到账号"}, status=404)
                    return

                if self.command == "POST":
                    payload = read_json_body(self)
                    cookie_str = payload.get("cookie", "")
                    cookie_items = []
                    for part in str(cookie_str or "").split(";"):
                        name, separator, value = part.strip().partition("=")
                        if separator:
                            cookie_items.append({"name": name.strip(), "value": value.strip()})
                    if not is_authenticated_bili_session(cookie_items):
                        self.send_json({"ok": False, "error": "登录凭据不完整"}, status=400)
                        return
                    uid = next((item["value"] for item in cookie_items if item["name"] == "DedeUserID"), "")
                    get_store().update_account_login_status(account["id"], "ok", "登录状态有效")
                    get_store().update_account(account["id"], {
                        "cookie": cookie_str,
                        "uid": uid,
                        "login_status": "ok",
                    })
                    self.send_json({"ok": True, "message": "登录凭据已更新"})
                    return

                from playwright.sync_api import sync_playwright
                with AccountDirectoryLock(account["user_data_dir"]):
                    with sync_playwright() as playwright:
                        context = launch_persistent_browser_context(
                            playwright,
                            account["user_data_dir"],
                            load_app_config().get("browser", {}),
                            headless=True,
                        )
                        page = context.pages[0] if context.pages else context.new_page()
                        page.goto("https://www.bilibili.com/", wait_until="domcontentloaded", timeout=30000)
                        cookies = context.cookies("https://www.bilibili.com") or []
                        cookie_str = "; ".join(f"{c['name']}={c['value']}" for c in cookies)
                        if is_authenticated_bili_session(cookies):
                            profile = read_bili_profile(page, cookies)
                            get_store().update_account_login_status(account["id"], "ok", "登录状态有效")
                            get_store().update_account(account["id"], {
                                "cookie": cookie_str,
                                "login_status": "ok",
                                "uid": profile.get("uid") or "",
                                "nickname": profile.get("nickname") or "",
                                "avatarUrl": profile.get("avatarUrl") or "",
                            })
                        else:
                            cookie_str = ""
                            mark_account_login_required(account["id"])
                        context.close()
                if not cookie_str:
                    self.send_json({"ok": False, "error": "尚未完成登录"}, status=401)
                    return
                self.send_json({"ok": True, "cookie": cookie_str})
            except Exception as error:
                self.send_json({"ok": False, "error": str(error)}, status=500)
            return

        if parsed.path == "/api/templates":
            self.send_json({"ok": True, "templates": get_store().list_templates()})
            return

        if parsed.path == "/api/comment-libraries":
            self.send_json({"ok": True, "libraries": libraries_payload()})
            return

        if parsed.path == "/api/videos":
            query = parse_qs(parsed.query)
            status = (query.get("status") or [""])[0] or None
            self.send_json({"ok": True, "videos": get_store().list_videos(status=status)})
            return

        if parsed.path == "/api/comment-plan":
            query = parse_qs(parsed.query)
            try:
                max_count = max(0, min(50, int((query.get("maxCount") or ["0"])[0] or 0)))
            except Exception:
                max_count = 0
            account_ids = query.get("accountId") or query.get("accountIds") or []
            library_id = (query.get("libraryId") or query.get("library_id") or [""])[0]
            plan = get_store().build_comment_plan(max_count=max_count, account_ids=account_ids, library_id=library_id)
            self.send_json({"ok": True, "plan": plan})
            return

        if parsed.path == "/api/ledger":
            self.send_json({"ok": True, "ledger": get_store().list_ledger()})
            return

        if parsed.path == "/api/ledger/export":
            output_file = os.path.join(APP_DIR, "outputs", "ledger.xlsx")
            result = get_store().export_ledger_excel(output_file)
            self.send_json({"ok": True, **result})
            return

        if parsed.path.startswith("/api/jobs/"):
            job_id = parsed.path.rsplit("/", 1)[-1]
            job = get_jobs().get(job_id)
            if not job:
                self.send_json(self.structured_error("任务不存在", category="unknown", suggestion="检查 jobId 后重试"))
                return
            self.send_json(get_jobs().serialize(job))
            return

        if parsed.path.startswith("/api/events/"):
            job_id = parsed.path.rsplit("/", 1)[-1]
            if not get_jobs().get(job_id):
                self.send_sse(
                    "event: error\n"
                    + f"data: {json.dumps(self.structured_error('任务不存在'), ensure_ascii=False)}\n\n"
                )
                return
            self.send_sse(get_jobs().sse_payload(job_id))
            return

        if parsed.path == "/api/bili-video":
            query = parse_qs(parsed.query)
            video_url = (query.get("url") or [""])[0]
            try:
                self.send_json({"ok": True, "video": fetch_bili_video(video_url)})
            except Exception as error:
                self.send_json({"ok": False, "error": str(error)}, status=502)
            return
        super().do_GET()

    def do_POST(self):
        parsed = urlparse(self.path)
        if parsed.path == "/api/ui-sessions/register":
            self.send_json(register_ui_session(read_json_body(self)))
            return

        if parsed.path == "/api/ui-sessions/close":
            self.send_json(close_ui_session(read_json_body(self), self.server))
            return

        if parsed.path == "/api/accounts":
            try:
                payload = read_json_body(self)
                account = get_store().create_account(payload, base_dir=APP_DIR)
                self.send_json({"ok": True, "account": account})
            except Exception as error:
                self.send_json(self.structured_error(str(error), category="api_error", suggestion="检查账号名称后重试"))
            return

        if parsed.path == "/api/ai/generate-comment":
            try:
                payload = read_json_body(self)
                provider = payload.get("provider", "deepseek").lower()
                rules = payload.get("rules", "")
                video_title = payload.get("videoTitle", "")

                settings = get_store().get_settings(include_secrets=True)

                result = generate_ai_comment(
                    provider,
                    video_title,
                    rules,
                    settings=settings,
                )

                self.send_json({"ok": True, "comment": result})
            except Exception as error:
                self.send_json({"ok": False, "error": str(error)}, status=200)
            return

        if parsed.path == "/api/settings/ai":
            try:
                payload = read_json_body(self)
                current = get_store().get_settings(include_secrets=True)
                if "deepseekApiKey" in payload and not str(payload.get("deepseekApiKey") or "").strip():
                    payload.pop("deepseekApiKey")
                if "qwenApiKey" in payload and not str(payload.get("qwenApiKey") or "").strip():
                    payload.pop("qwenApiKey")
                settings = get_store().save_ai_settings(payload)
                self.send_json({"ok": True, "settings": settings})
            except Exception as error:
                self.send_json(self.structured_error(str(error), category="api_error", suggestion="检查 API Key 和模型名称后重试"))
            return

        if parsed.path == "/api/settings/ai/verify-deepseek":
            try:
                payload = read_json_body(self)
                settings = get_store().get_settings(include_secrets=True)
                api_key = payload.get("apiKey") or settings["deepseek"].get("apiKey")
                model = payload.get("model") or settings["deepseek"].get("model") or DEFAULT_DEEPSEEK_MODEL
                base_url = payload.get("baseUrl") or settings["deepseek"].get("baseUrl")
                if not api_key:
                    raise ValueError("请先保存 DeepSeek API Key")
                result = deepseek_chat(
                    api_key,
                    model,
                    [
                        {"role": "system", "content": "只输出 JSON。"},
                        {"role": "user", "content": "{\"ok\": true, \"message\": \"连接正常\"}"},
                    ],
                    max_tokens=80,
                    base_url=base_url,
                )
                self.send_json({"ok": True, "provider": "deepseek", "model": model, "result": result})
            except Exception as error:
                self.send_json(self.structured_error(str(error), category="api_error", suggestion="检查 DeepSeek Key、余额或模型名称"))
            return

        if parsed.path == "/api/settings/ai/verify-qwen":
            try:
                payload = read_json_body(self)
                settings = get_store().get_settings(include_secrets=True)
                api_key = payload.get("apiKey") or settings["qwen"].get("apiKey")
                model = payload.get("model") or settings["qwen"].get("model") or DEFAULT_QWEN_MODEL
                base_url = payload.get("baseUrl") or settings["qwen"].get("baseUrl")
                if not api_key:
                    raise ValueError("请先保存千问 API Key")
                result = qwen_chat(
                    api_key,
                    model,
                    [
                        {"role": "user", "content": "请用 JSON 回复：{\"ok\": true, \"message\": \"连接正常\"}"},
                    ],
                    max_tokens=80,
                    base_url=base_url,
                )
                self.send_json({"ok": True, "provider": "qwen", "model": model, "result": result})
            except Exception as error:
                self.send_json(self.structured_error(str(error), category="api_error", suggestion="检查千问 Key、余额或模型名称"))
            return

        if parsed.path == "/api/keywords/normalize":
            try:
                self.send_json(normalize_keywords_for_payload(read_json_body(self)))
            except Exception as error:
                self.send_json(self.structured_error(str(error), category="api_error", suggestion="检查关键词内容后重试"))
            return

        if parsed.path == "/api/videos/clear":
            result = get_store().clear_video_queue()
            self.send_json({"ok": True, **result})
            return

        if parsed.path == "/api/ledger/clear":
            result = get_store().clear_ledger()
            self.send_json({"ok": True, **result})
            return

        if parsed.path.startswith("/api/ledger/") and parsed.path.endswith("/delete"):
            try:
                parts = parsed.path.strip("/").split("/")
                ledger_id = int(parts[2]) if len(parts) >= 4 else 0
                if ledger_id <= 0:
                    raise ValueError("invalid ledger id")
                result = get_store().delete_ledger_entry(ledger_id)
                self.send_json({"ok": True, **result})
            except Exception as error:
                self.send_json(self.structured_error(str(error), category="api_error", suggestion="检查台账记录后重试"))
            return

        if parsed.path == "/api/actions/import-videos":
            self.start_action("import_videos", action_import_videos)
            return

        if parsed.path == "/api/actions/search-videos":
            self.start_action("search_videos", action_search_videos)
            return

        if parsed.path == "/api/actions/run-comment-workflow":
            self.start_action("run_comment_workflow", action_run_comment_workflow)
            return

        if parsed.path == "/api/actions/run-integrated-workflow":
            self.start_action("run_integrated_workflow", action_run_integrated_workflow)
            return

        if parsed.path == "/api/actions/login-account":
            self.start_action("login_account", action_login_account)
            return

        if parsed.path == "/api/actions/check-account-login":
            self.start_action("check_account_login", action_check_account_login)
            return

        if parsed.path.startswith("/api/jobs/") and parsed.path.endswith("/stop"):
            parts = parsed.path.strip("/").split("/")
            job_id = parts[2] if len(parts) >= 3 else ""
            job = get_jobs().request_stop(job_id)
            if not job:
                self.send_json(self.structured_error("任务不存在", category="unknown", suggestion="检查 jobId 后重试"))
                return
            self.send_json(get_jobs().serialize(job))
            return

        if parsed.path == "/api/templates":
            try:
                payload = read_json_body(self)
                templates = payload.get("templates") or []
                if isinstance(templates, str):
                    templates = templates.splitlines()
                result = get_store().save_templates(templates)
                self.send_json({"ok": True, **result})
            except Exception as error:
                self.send_json({"ok": False, "error": str(error)}, status=200)
            return

        if parsed.path == "/api/comment-libraries":
            try:
                payload = read_json_body(self)
                templates = payload.get("templates") or []
                if isinstance(templates, str):
                    templates = templates.splitlines()
                library_id = int(payload.get("id") or 0)
                if library_id > 0:
                    library = get_store().update_comment_library(
                        library_id,
                        payload.get("name") or payload.get("libraryName"),
                        templates,
                        enabled=payload.get("enabled", True),
                    )
                else:
                    library = get_store().save_comment_library(
                        payload.get("name") or payload.get("libraryName"),
                        templates,
                        enabled=payload.get("enabled", True),
                    )
                library = {
                    **library,
                    "templates": get_store().list_library_templates(library["id"], enabled_only=True),
                }
                self.send_json({"ok": True, "library": library, "libraries": libraries_payload()})
            except Exception as error:
                self.send_json(self.structured_error(str(error), category="api_error", suggestion="检查词库名称和评论内容后重试"))
            return

        if parsed.path == "/api/comment-libraries/import-directory":
            try:
                payload = read_json_body(self)
                directory = payload.get("directory") or payload.get("templateDir") or ""
                result = get_store().import_comment_libraries_from_directory(directory)
                self.send_json({"ok": True, **result, "libraries": libraries_payload()})
            except Exception as error:
                self.send_json(self.structured_error(str(error), category="api_error", suggestion="检查评论词文件夹路径后重试"))
            return

        if parsed.path == "/api/comment-libraries/import-files":
            try:
                payload = read_json_body(self)
                imported = 0
                for item in (payload.get("files") or [])[:200]:
                    relative_name = str(item.get("name") or "").replace("\\", "/").strip()
                    if not relative_name.lower().endswith(".txt"):
                        continue
                    library_name = os.path.splitext(os.path.basename(relative_name))[0].strip()
                    templates = [line.strip() for line in str(item.get("content") or "").splitlines() if line.strip()]
                    if not library_name or not templates:
                        continue
                    get_store().save_comment_library(library_name, templates, enabled=True)
                    imported += 1
                self.send_json({"ok": True, "imported": imported, "libraries": libraries_payload()})
            except Exception as error:
                self.send_json(self.structured_error(str(error), category="api_error", suggestion="请检查所选词库文件后重试"))
            return

        if parsed.path == "/api/deepseek-test":
            payload = {}
            try:
                payload = read_json_body(self)
                api_key = payload.get("apiKey")
                model = payload.get("model") or DEFAULT_DEEPSEEK_MODEL
                if not api_key:
                    raise ValueError("请先填写 DeepSeek API Key")
                result = deepseek_chat(
                    api_key,
                    model,
                    [
                        {"role": "system", "content": "你只输出 JSON。"},
                        {"role": "user", "content": "{\"ok\": true, \"message\": \"连接成功\"}"},
                    ],
                    max_tokens=80,
                    base_url=payload.get("baseUrl"),
                )
                self.send_json({"ok": True, "result": result})
            except Exception as error:
                self.send_json({"ok": False, "error": str(error)}, status=200)
            return

        if parsed.path == "/api/qwen-test":
            payload = {}
            try:
                payload = read_json_body(self)
                api_key = payload.get("apiKey")
                model = payload.get("model") or DEFAULT_QWEN_MODEL
                if not api_key:
                    raise ValueError("请先填写千问 API Key")
                result = qwen_chat(
                    api_key,
                    model,
                    [
                        {"role": "user", "content": "请用一句中文回复：千问连接正常"},
                    ],
                    max_tokens=80,
                    json_mode=False,
                    base_url=payload.get("baseUrl"),
                )
                self.send_json({"ok": True, "result": result})
            except Exception as error:
                self.send_json({"ok": False, "error": str(error)}, status=200)
            return

        if parsed.path == "/api/deepseek-match":
            payload = {}
            try:
                payload = read_json_body(self)
                api_key = payload.get("apiKey")
                model = payload.get("model") or DEFAULT_DEEPSEEK_MODEL
                if not api_key:
                    raise ValueError("请先填写 DeepSeek API Key")
                result = deepseek_chat(api_key, model, build_deepseek_messages(payload), base_url=payload.get("baseUrl"))
                self.send_json({"ok": True, "result": result})
            except Exception as error:
                self.send_json({"ok": False, "error": str(error)}, status=200)
            return

        if parsed.path == "/api/qwen-comment-ocr":
            try:
                images, fields = read_multipart_form(self)
                api_key = self.headers.get("X-Qwen-Api-Key") or ""
                model = self.headers.get("X-Qwen-Model") or DEFAULT_QWEN_MODEL
                if not api_key:
                    raise ValueError("请先填写千问 API Key")
                result = qwen_chat(
                    api_key,
                    model,
                    build_qwen_comment_ocr_messages(images, fields.get("rules") or ""),
                    max_tokens=3000,
                    base_url=self.headers.get("X-Qwen-Base-Url") or fields.get("baseUrl") or "",
                )
                self.send_json({"ok": True, "result": result})
            except Exception as error:
                self.send_json({"ok": False, "error": str(error)}, status=200)
            return

        if parsed.path == "/api/deepseek-discover":
            payload = {}
            try:
                payload = read_json_body(self)
                api_key = payload.get("apiKey")
                model = payload.get("model") or DEFAULT_DEEPSEEK_MODEL
                keywords = [item.strip() for item in (payload.get("keywords") or []) if item.strip()]
                if not api_key:
                    raise ValueError("请先填写 DeepSeek API Key")
                if not keywords:
                    raise ValueError("请先填写监控关键词")
                year = payload.get("year") or ""
                try:
                    limit = max(1, min(20, int(payload.get("limit") or 5)))
                except Exception:
                    limit = 5
                candidates = search_bili_videos(keywords, per_keyword=max(8, limit * 3), year=year, max_pages=5)
                if not candidates:
                    self.send_json({"ok": True, "items": [], "candidates": 0})
                    return
                result = deepseek_chat(
                    api_key,
                    model,
                    build_discovery_messages(payload, candidates),
                    max_tokens=1800,
                    base_url=payload.get("baseUrl"),
                )
                items = filter_discovery_items(result, candidates, payload.get("existing") or [], limit=limit)
                self.send_json({"ok": True, "items": items, "candidates": len(candidates)})
            except Exception as error:
                self.send_json({"ok": False, "error": str(error)}, status=200)
            return

        if parsed.path == "/api/executor-links":
            try:
                payload = read_json_body(self)
                items = payload.get("items") or []
                if not isinstance(items, list):
                    raise ValueError("items 必须是数组")
                output_file = os.path.join(APP_DIR, "data", "video_links.xlsx")
                count = write_video_links_excel(items, output_file)
                if count <= 0:
                    raise ValueError("没有可导出的有效视频链接")
                self.send_json({"ok": True, "count": count, "file": output_file})
            except Exception as error:
                self.send_json({"ok": False, "error": str(error)}, status=200)
            return

        self.send_error(404, "File not found")

    def do_PATCH(self):
        parsed = urlparse(self.path)
        account_id = _account_id_from_path(parsed.path)
        if account_id is not None:
            try:
                payload = read_json_body(self)
                account = get_store().update_account(account_id, payload)
                self.send_json({"ok": True, "account": account})
            except Exception as error:
                self.send_json(self.structured_error(str(error), category="api_error", suggestion="检查账号字段后重试"))
            return
        self.send_error(404, "File not found")

    def do_DELETE(self):
        parsed = urlparse(self.path)
        video_id = _video_id_from_path(parsed.path)
        if video_id is not None:
            try:
                result = get_store().delete_video(video_id)
                if not result.get("deleted"):
                    raise ValueError("候选视频不存在")
                self.send_json({"ok": True, **result})
            except Exception as error:
                self.send_json(self.structured_error(str(error), category="api_error", suggestion="刷新发现池后重试"))
            return

        library_id = _comment_library_id_from_path(parsed.path)
        if library_id is not None:
            try:
                result = get_store().delete_comment_library(library_id)
                if not result.get("deleted"):
                    raise ValueError("评论词库不存在")
                self.send_json({"ok": True, **result, "libraries": libraries_payload()})
            except Exception as error:
                self.send_json(self.structured_error(str(error), category="api_error", suggestion="刷新评论词库后重试"))
            return

        account_id = _account_id_from_path(parsed.path)
        if account_id is not None:
            try:
                account = get_store().get_account(account_id)
                deleted = get_store().delete_account(account_id)
                if not deleted:
                    raise ValueError("账号不存在")
                remove_managed_account_profile(account)
                self.send_json({"ok": True, "deleted": True})
            except Exception as error:
                self.send_json(self.structured_error(str(error), category="api_error", suggestion="刷新账号列表后重试"))
            return
        self.send_error(404, "File not found")


if __name__ == "__main__":
    server = ExclusiveThreadingHTTPServer((HOST, PORT), Handler)
    parent_process_id = os.environ.get("BIBI_PARENT_PID", "").strip()
    if parent_process_id:
        threading.Thread(
            target=monitor_parent_process,
            args=(server, parent_process_id),
            daemon=True,
        ).start()
    print(f"Serving B站互助台 at http://{HOST}:{PORT}/index.html")
    try:
        server.serve_forever()
    finally:
        server.server_close()
