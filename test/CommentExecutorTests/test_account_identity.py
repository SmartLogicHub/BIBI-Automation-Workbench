import sys
import tempfile
import unittest
import json
import os
import io
import threading
from pathlib import Path
from unittest.mock import patch


EXECUTOR_DIR = Path(__file__).resolve().parents[2] / "src" / "Ray.BiliBiliTool.Web" / "Executor"
sys.path.insert(0, str(EXECUTOR_DIR))

from app_store import AppStore, normalize_deepseek_model, now_iso
from bili_operator import OperatorResult
import bili_operator
import server


class AccountIdentityTests(unittest.TestCase):
    def test_guest_cookies_are_not_treated_as_a_logged_in_account(self):
        guest_cookies = [
            {"name": "buvid3", "value": "guest-device"},
            {"name": "b_nut", "value": "guest-timestamp"},
        ]
        authenticated_cookies = [
            {"name": "DedeUserID", "value": "123456"},
            {"name": "SESSDATA", "value": "session"},
            {"name": "bili_jct", "value": "csrf"},
        ]

        self.assertFalse(server.is_authenticated_bili_session(guest_cookies))
        self.assertTrue(server.is_authenticated_bili_session(authenticated_cookies))

    def test_missing_login_clears_guest_cookie_and_records_check_time(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            store = AppStore(root / "app.db")
            store.initialize()
            account = store.create_account({"name": "待登录账号"}, base_dir=root)
            store.update_account(account["id"], {
                "cookie": "buvid3=guest-device; b_nut=guest-timestamp",
                "loginStatus": "checking",
            })

            previous_store = server.APP_STORE
            server.APP_STORE = store
            try:
                updated = server.mark_account_login_required(account["id"])
            finally:
                server.APP_STORE = previous_store

            self.assertEqual(updated["login_status"], "login_required")
            self.assertEqual(updated["cookie"], "")
            self.assertTrue(updated["last_login_checked_at"])

    def test_profile_identity_is_persisted_and_uid_is_unique(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            store = AppStore(root / "app.db")
            store.initialize()
            first = store.create_account({"name": "主账号"}, base_dir=root)
            second = store.create_account({"name": "备用账号"}, base_dir=root)

            updated = store.update_account(first["id"], {
                "uid": "123456",
                "nickname": "测试用户",
                "avatarUrl": "https://i.example/avatar.jpg",
                "cookie": "SESSDATA=test",
                "loginStatus": "ok",
            })

            self.assertEqual(updated["uid"], "123456")
            self.assertEqual(updated["nickname"], "测试用户")
            self.assertEqual(updated["avatar_url"], "https://i.example/avatar.jpg")
            with self.assertRaisesRegex(ValueError, "已经托管"):
                store.update_account(second["id"], {"uid": "123456"})

    def test_comment_plan_rotates_only_between_logged_in_accounts(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            store = AppStore(root / "app.db")
            store.initialize()
            first = store.create_account({"name": "账号一"}, base_dir=root)
            second = store.create_account({"name": "账号二"}, base_dir=root)
            signed_out = store.create_account({"name": "未登录账号"}, base_dir=root)
            store.update_account(first["id"], {
                "uid": "1001",
                "cookie": "DedeUserID=1001; SESSDATA=first; bili_jct=csrf1",
                "loginStatus": "ok",
            })
            store.update_account(second["id"], {
                "uid": "1002",
                "cookie": "DedeUserID=1002; SESSDATA=second; bili_jct=csrf2",
                "loginStatus": "ok",
            })
            store.update_account(signed_out["id"], {"loginStatus": "login_required"})
            library = store.save_comment_library("轮询词库", ["评论一", "评论二"])
            store.import_videos(
                ["BV1xx411c7mD", "BV1Q541167Qg", "BV1mK4y1C7Bz", "BV1GJ411x7h7"],
                source="manual",
                dry_run=False,
            )

            plan = store.build_comment_plan(max_count=4, library_id=library["id"])

            assigned = [item["account_id"] for item in plan["items"]]
            self.assertEqual(assigned, [first["id"], second["id"], first["id"], second["id"]])
            self.assertNotIn(signed_out["id"], assigned)

    def test_daily_comment_quota_resets_when_calendar_day_changes(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            store = AppStore(root / "app.db")
            store.initialize()
            account = store.create_account({"name": "跨日账号", "dailyLimit": 1}, base_dir=root)
            store.update_account(account["id"], {
                "uid": "2001",
                "cookie": "DedeUserID=2001; SESSDATA=session; bili_jct=csrf",
                "loginStatus": "ok",
            })
            with store.connect() as db:
                columns = {row["name"] for row in db.execute("PRAGMA table_info(accounts)").fetchall()}
            self.assertIn("today_count_date", columns)
            with store.connect() as db:
                db.execute(
                    "UPDATE accounts SET today_count=1, today_count_date='2000-01-01' WHERE id=?",
                    (account["id"],),
                )

            refreshed = store.get_account(account["id"])

            self.assertEqual(refreshed["today_count"], 0)
            self.assertEqual(refreshed["today_count_date"], now_iso()[:10])
            self.assertEqual([item["id"] for item in store._eligible_accounts([])], [account["id"]])

    def test_summary_counts_only_logged_in_accounts_as_available(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            store = AppStore(root / "app.db")
            store.initialize()
            available = store.create_account({"name": "可用账号"}, base_dir=root)
            signed_out = store.create_account({"name": "待登录账号"}, base_dir=root)
            store.update_account(available["id"], {
                "uid": "2101",
                "cookie": "DedeUserID=2101; SESSDATA=session; bili_jct=csrf",
                "loginStatus": "ok",
            })
            store.update_account(signed_out["id"], {"loginStatus": "login_required"})

            summary = store.summary()

            self.assertEqual(summary["account_count"], 2)
            self.assertEqual(summary["available_account_count"], 1)

    def test_account_profile_paths_are_unique_after_slug_collision(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            store = AppStore(root / "app.db")
            store.initialize()

            first = store.create_account({"name": "账号/一"}, base_dir=root)
            second = store.create_account({"name": "账号\\一"}, base_dir=root)

            self.assertNotEqual(Path(first["user_data_dir"]), Path(second["user_data_dir"]))

    def test_login_required_publish_result_returns_video_to_pending_queue(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            store = AppStore(root / "app.db")
            store.initialize()
            account = store.create_account({"name": "掉线账号"}, base_dir=root)
            store.update_account(account["id"], {
                "uid": "3001",
                "cookie": "DedeUserID=3001; SESSDATA=session; bili_jct=csrf",
                "loginStatus": "ok",
            })
            library = store.save_comment_library("重试词库", ["稍后重试"])
            store.import_videos(["BV1xx411c7mD"], source="manual", dry_run=False)
            item = store.build_comment_plan(max_count=1, library_id=library["id"])["items"][0]

            store.record_publish_result(item, server.STATUS_LOGIN_REQUIRED, "登录已失效")

            video = store.list_videos(limit=1)[0]
            self.assertEqual(video["status"], "pending")
            self.assertEqual(video["last_error"], "登录已失效")

    def test_browser_search_tries_all_available_accounts_until_one_succeeds(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            store = AppStore(root / "app.db")
            store.initialize()
            for index in range(1, 5):
                account = store.create_account({"name": f"account-{index}"}, base_dir=root)
                store.update_account(account["id"], {
                    "uid": str(4000 + index),
                    "cookie": f"DedeUserID={4000 + index}; SESSDATA=session-{index}; bili_jct=csrf-{index}",
                    "loginStatus": "ok",
                })

            class SearchOperator:
                def __init__(self, *_args, **_kwargs):
                    pass

                def search_videos_with_browser(self, account, *_args, **_kwargs):
                    if account.name != "account-4":
                        raise RuntimeError(f"{account.name} unavailable")
                    return [{"bvid": "BV1xx411c7mD", "url": "https://www.bilibili.com/video/BV1xx411c7mD"}]

            previous_store = server.APP_STORE
            server.APP_STORE = store
            try:
                with (
                    patch.object(server, "BiliOperator", SearchOperator),
                    patch.object(server, "load_app_config", return_value={"browser": {}, "comment": {}}),
                ):
                    results = server.search_bili_videos_with_browser_accounts(["耳机"], per_keyword=1)
            finally:
                server.APP_STORE = previous_store

            self.assertEqual([item["bvid"] for item in results], ["BV1xx411c7mD"])

    def test_comment_workflow_continues_with_next_account_after_one_login_expires(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            store = AppStore(root / "app.db")
            store.initialize()
            first = store.create_account({"name": "account-1"}, base_dir=root)
            second = store.create_account({"name": "account-2"}, base_dir=root)
            for account, uid in ((first, "5001"), (second, "5002")):
                store.update_account(account["id"], {
                    "uid": uid,
                    "cookie": f"DedeUserID={uid}; SESSDATA=session-{uid}; bili_jct=csrf-{uid}",
                    "loginStatus": "ok",
                })
            library = store.save_comment_library("双账号词库", ["自然评论"])
            store.import_videos(
                ["BV1xx411c7mD", "BV1Q541167Qg"],
                source="manual",
                dry_run=False,
            )

            class PublishOperator:
                def __init__(self, *_args, **_kwargs):
                    pass

                def publish_comment(self, account, *_args, **_kwargs):
                    if account.name == "account-1":
                        return OperatorResult(server.STATUS_LOGIN_REQUIRED, "登录已失效")
                    return OperatorResult(
                        server.STATUS_SUCCESS,
                        verification_method="comment_visible",
                        proof_text="评论已出现在视频评论区",
                    )

            class JobState:
                stop_requested = False

            class TestJobs:
                def get(self, _job_id):
                    return JobState()

                def update(self, *_args, **_kwargs):
                    return None

            previous_store = server.APP_STORE
            previous_jobs = server.JOBS
            server.APP_STORE = store
            server.JOBS = TestJobs()
            try:
                with (
                    patch.object(server, "BiliOperator", PublishOperator),
                    patch.object(
                        server,
                        "load_app_config",
                        return_value={
                            "browser": {},
                            "comment": {},
                            "scheduler": {
                                "account_interval_min_seconds": 0,
                                "account_interval_max_seconds": 0,
                                "same_account_cooldown_min_seconds": 0,
                                "same_account_cooldown_max_seconds": 0,
                            },
                        },
                    ),
                ):
                    result = server.action_run_comment_workflow(
                        type("Job", (), {"id": "multi-account-job"})(),
                        {
                            "dryRun": False,
                            "maxCount": 2,
                            "libraryId": library["id"],
                        },
                    )
            finally:
                server.APP_STORE = previous_store
                server.JOBS = previous_jobs

            self.assertEqual(result["success"], 1)
            self.assertEqual(result["failed"], 1)
            self.assertEqual([item["status"] for item in result["items"]], [
                server.STATUS_LOGIN_REQUIRED,
                server.STATUS_SUCCESS,
            ])

    def test_integrated_workflow_imports_all_manual_videos_before_discovery(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            store = AppStore(Path(temporary_directory) / "app.db")
            store.initialize()
            library = store.save_comment_library("指定视频词库", ["自然评论"])
            discovery_state = {}

            class JobState:
                stop_requested = False

            class TestJobs:
                def get(self, _job_id):
                    return JobState()

                def update(self, *_args, **_kwargs):
                    return None

            def fake_discovery(_payload, _dry_run):
                discovery_state["pending_before"] = store.summary()["queue_pending"]
                return {
                    "total": 0,
                    "inserted": 0,
                    "duplicate": 0,
                    "invalid": 0,
                    "items": [],
                }

            previous_store = server.APP_STORE
            previous_jobs = server.JOBS
            server.APP_STORE = store
            server.JOBS = TestJobs()
            try:
                with (
                    patch.object(server, "discover_videos_for_payload", side_effect=fake_discovery),
                    patch.object(
                        server,
                        "action_run_comment_workflow",
                        return_value={"total": 0, "success": 0, "pendingConfirmation": 0, "failed": 0, "items": []},
                    ),
                    patch.object(server, "load_app_config", return_value={"scheduler": {}}),
                ):
                    result = server.action_run_integrated_workflow(
                        type("Job", (), {"id": "manual-import-job"})(),
                        {
                            "dryRun": False,
                            "keywords": ["耳机"],
                            "libraryId": library["id"],
                            "maxCount": 2,
                            "manualUrls": ["BV1xx411c7mD", "BV1Q541167Qg"],
                            "accountIntervalMinSeconds": 1,
                            "accountIntervalMaxSeconds": 1,
                            "sameAccountCooldownMinSeconds": 1,
                            "sameAccountCooldownMaxSeconds": 1,
                        },
                    )
            finally:
                server.APP_STORE = previous_store
                server.JOBS = previous_jobs

            self.assertEqual(discovery_state["pending_before"], 2)
            self.assertEqual({item["bvid"] for item in store.list_videos()}, {"BV1xx411c7mD", "BV1Q541167Qg"})
            self.assertEqual(result["manualImport"]["inserted"], 2)

    def test_integrated_workflow_treats_max_count_as_the_whole_run_limit(self):
        class JobState:
            stop_requested = False

        class TestJobs:
            def get(self, _job_id):
                return JobState()

            def update(self, *_args, **_kwargs):
                return None

        clock = {"value": 0}

        def advancing_monotonic():
            clock["value"] += 5
            return clock["value"]

        publish_result = {
            "total": 1,
            "success": 1,
            "pendingConfirmation": 0,
            "failed": 0,
            "items": [{"status": server.STATUS_SUCCESS}],
        }
        previous_jobs = server.JOBS
        server.JOBS = TestJobs()
        try:
            with (
                patch.object(server, "load_app_config", return_value={"scheduler": {}}),
                patch.object(server.time, "monotonic", side_effect=advancing_monotonic),
                patch.object(server, "_wait_with_stop", return_value=True),
                patch.object(
                    server,
                    "discover_videos_for_payload",
                    return_value={"total": 0, "inserted": 0, "duplicate": 0, "invalid": 0, "items": []},
                ),
                patch.object(server, "action_run_comment_workflow", return_value=publish_result) as publish,
            ):
                result = server.action_run_integrated_workflow(
                    type("Job", (), {"id": "whole-run-limit-job"})(),
                    {
                        "dryRun": False,
                        "durationSeconds": 60,
                        "cycleIntervalSeconds": 1,
                        "keywords": ["耳机"],
                        "libraryId": "1",
                        "maxCount": 2,
                        "accountIntervalMinSeconds": 1,
                        "accountIntervalMaxSeconds": 1,
                        "sameAccountCooldownMinSeconds": 1,
                        "sameAccountCooldownMaxSeconds": 1,
                    },
                )
        finally:
            server.JOBS = previous_jobs

        self.assertEqual(result["published"], 2)
        self.assertEqual(publish.call_count, 2)
        self.assertEqual(result["cycles"], 1)

    def test_account_profile_paths_are_rebased_after_the_app_is_moved(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            old_app_dir = root / "old-release"
            new_app_dir = root / "new-release"
            store = AppStore(new_app_dir / "data" / "app.db")
            store.initialize()
            account = store.create_account({"name": "主账号"}, base_dir=old_app_dir)

            expected_profile = (new_app_dir / "accounts" / "主账号").resolve()
            store.rebase_account_directories(new_app_dir)
            rebased = store.get_account(account["id"])

            self.assertEqual(Path(rebased["user_data_dir"]), expected_profile)

    def test_ai_fallback_uses_selected_library_and_never_a_fixed_comment(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            store = AppStore(Path(temporary_directory) / "app.db")
            store.initialize()
            library = store.save_comment_library("自然短评", ["很有意思", "讲得很清楚"])
            previous_store = server.APP_STORE
            server.APP_STORE = store
            try:
                self.assertIn(server.fallback_comment_from_library(library["id"]), {"很有意思", "讲得很清楚"})
                self.assertEqual(server.fallback_comment_from_library(""), "")
            finally:
                server.APP_STORE = previous_store

    def test_comment_library_edit_updates_the_same_library(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            store = AppStore(Path(temporary_directory) / "app.db")
            store.initialize()
            library = store.save_comment_library("原词库", ["第一条"])

            updated = store.update_comment_library(
                library["id"],
                "新词库",
                ["更新后的评论", "第二条评论"],
            )

            self.assertEqual(updated["id"], library["id"])
            self.assertEqual(updated["name"], "新词库")
            self.assertEqual(
                [item["text"] for item in store.list_library_templates(library["id"], enabled_only=True)],
                ["更新后的评论", "第二条评论"],
            )
            self.assertEqual(len(store.list_comment_libraries()), 1)

    def test_discovery_video_can_be_deleted_without_clearing_the_pool(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            store = AppStore(Path(temporary_directory) / "app.db")
            store.initialize()
            store.import_videos(
                ["BV1xx411c7mD", "BV1Q541167Qg"],
                source="manual",
                dry_run=False,
            )
            videos = store.list_videos()

            result = store.delete_video(videos[0]["id"])

            self.assertEqual(result["deleted"], 1)
            remaining = store.list_videos()
            self.assertEqual(len(remaining), 1)
            self.assertNotEqual(remaining[0]["id"], videos[0]["id"])

    def test_plain_text_ai_comment_disables_json_mode_and_returns_text(self):
        settings = {
            "deepseek": {
                "apiKey": "test-key",
                "model": "deepseek-chat",
                "baseUrl": "https://api.deepseek.com",
            },
            "qwen": {},
        }
        with patch.object(server, "deepseek_chat", return_value="这段讲得很清楚") as chat:
            result = server.generate_ai_comment(
                "deepseek",
                "测试视频",
                "不超过三十字",
                settings=settings,
            )

        self.assertEqual(result, "这段讲得很清楚")
        self.assertFalse(chat.call_args.kwargs["json_mode"])

    def test_plain_chat_response_does_not_request_json_object(self):
        response_payload = {
            "choices": [{"message": {"content": "自然评论文本"}}],
        }

        class FakeResponse:
            def __enter__(self):
                return self

            def __exit__(self, exc_type, exc, traceback):
                return False

            def read(self):
                return json.dumps(response_payload).encode("utf-8")

        captured = {}

        def fake_urlopen(request, timeout):
            captured["payload"] = json.loads(request.data.decode("utf-8"))
            return FakeResponse()

        with patch.object(server, "urlopen", side_effect=fake_urlopen):
            result = server.deepseek_chat(
                "test-key",
                "deepseek-chat",
                [{"role": "user", "content": "生成一句评论"}],
                json_mode=False,
            )

        self.assertEqual(result, "自然评论文本")
        self.assertNotIn("response_format", captured["payload"])

    def test_login_navigation_failure_keeps_browser_session_available(self):
        class FailingPage:
            def goto(self, *args, **kwargs):
                raise RuntimeError("net::ERR_NETWORK_CHANGED")

        warning = server.open_login_landing_page(FailingPage())

        self.assertIn("登录窗口已经打开", warning)
        self.assertIn("地址栏", warning)

    def test_browser_operator_uses_the_shared_account_directory_lock(self):
        self.assertIs(bili_operator.AccountDirectoryLock, server.AccountDirectoryLock)

    def test_failed_comment_attempt_does_not_trigger_account_rotation_wait(self):
        self.assertFalse(
            server.should_wait_after_comment_attempt(
                {"success": 0, "pendingConfirmation": 0, "failed": 1}
            )
        )
        self.assertTrue(
            server.should_wait_after_comment_attempt(
                {"success": 1, "pendingConfirmation": 0, "failed": 0}
            )
        )
        self.assertTrue(
            server.should_wait_after_comment_attempt(
                {"success": 0, "pendingConfirmation": 1, "failed": 0}
            )
        )

    def test_wait_is_capped_by_the_remaining_run_time(self):
        self.assertEqual(server.cap_wait_to_deadline(600, deadline=125.0, now=100.0), 25)
        self.assertEqual(server.cap_wait_to_deadline(20, deadline=125.0, now=100.0), 20)
        self.assertEqual(server.cap_wait_to_deadline(20, deadline=None, now=100.0), 20)

    def test_authenticated_login_session_is_persisted_for_automatic_window_close(self):
        class AuthenticatedContext:
            @staticmethod
            def cookies(_url):
                return [
                    {"name": "DedeUserID", "value": "123456"},
                    {"name": "SESSDATA", "value": "session"},
                    {"name": "bili_jct", "value": "csrf"},
                ]

        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            store = AppStore(root / "app.db")
            store.initialize()
            account = store.create_account({"name": "登录账号"}, base_dir=root)
            previous_store = server.APP_STORE
            server.APP_STORE = store
            try:
                with patch.object(
                    server,
                    "read_bili_profile",
                    return_value={
                        "uid": "123456",
                        "nickname": "真实昵称",
                        "avatarUrl": "https://i.example/avatar.jpg",
                    },
                ):
                    result = server.capture_authenticated_login_session(
                        account,
                        AuthenticatedContext(),
                        object(),
                    )
            finally:
                server.APP_STORE = previous_store

            self.assertEqual(result["loginStatus"], "ok")
            self.assertEqual(result["account"]["uid"], "123456")
            self.assertIn("SESSDATA=session", result["cookie"])

    def test_executor_server_rejects_a_second_listener_on_the_same_port(self):
        first = server.ExclusiveThreadingHTTPServer(("127.0.0.1", 0), server.Handler)
        try:
            port = first.server_address[1]
            with self.assertRaises(OSError):
                server.ExclusiveThreadingHTTPServer(("127.0.0.1", port), server.Handler)
        finally:
            first.server_close()

    def test_parent_process_liveness_check(self):
        self.assertTrue(server.process_is_alive(os.getpid()))
        self.assertFalse(server.process_is_alive(2_147_483_000))

    @unittest.skipUnless(os.name == "nt", "Windows-specific process probing")
    def test_windows_liveness_check_never_sends_a_signal(self):
        with patch.object(server.os, "kill", side_effect=AssertionError("must not signal")):
            self.assertTrue(server.process_is_alive(os.getpid()))

    def test_structured_errors_are_never_reported_as_success(self):
        payload = server.Handler.structured_error(None, "create failed")

        self.assertIs(payload["ok"], False)
        self.assertEqual(payload["status"], "error")

    def test_json_body_supports_http_chunked_transfer(self):
        raw_json = json.dumps({"name": "页面账号"}, ensure_ascii=False).encode("utf-8")
        midpoint = len(raw_json) // 2
        chunks = (raw_json[:midpoint], raw_json[midpoint:])
        encoded = b"".join(
            f"{len(chunk):X}\r\n".encode("ascii") + chunk + b"\r\n"
            for chunk in chunks
        ) + b"0\r\n\r\n"

        class ChunkedHandler:
            headers = {"Transfer-Encoding": "chunked"}
            rfile = io.BytesIO(encoded)

        self.assertEqual(server.read_json_body(ChunkedHandler()), {"name": "页面账号"})

    def test_deepseek_model_normalization_migrates_retiring_aliases(self):
        self.assertEqual(normalize_deepseek_model("deepseek-chat"), "deepseek-v4-flash")
        self.assertEqual(normalize_deepseek_model("deepseek-reasoner"), "deepseek-v4-flash")
        self.assertEqual(normalize_deepseek_model("deepseek-v4-flash"), "deepseek-v4-flash")
        self.assertEqual(normalize_deepseek_model("deepseek-v4-pro"), "deepseek-v4-pro")

    def test_ai_provider_configuration_can_be_explicitly_removed(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            store = AppStore(Path(temporary_directory) / "app.db")
            store.initialize()
            store.save_ai_settings({"deepseek": {"apiKey": "test-secret"}})
            self.assertTrue(store.get_settings()["deepseek"]["configured"])

            store.save_ai_settings({"deepseek": {"clearApiKey": True}})

            self.assertFalse(store.get_settings()["deepseek"]["configured"])

    def test_deepseek_v4_comment_requests_disable_thinking(self):
        captured = {}

        class FakeResponse:
            def __enter__(self):
                return self

            def __exit__(self, *args):
                return False

            def read(self):
                return json.dumps(
                    {"choices": [{"message": {"content": "自然评论文本"}}]}
                ).encode("utf-8")

        def fake_urlopen(request, timeout):
            captured["payload"] = json.loads(request.data.decode("utf-8"))
            return FakeResponse()

        with patch.object(server, "urlopen", side_effect=fake_urlopen):
            result = server.deepseek_chat(
                "test-key",
                "deepseek-v4-flash",
                [{"role": "user", "content": "生成一句评论"}],
                json_mode=False,
            )

        self.assertEqual(result, "自然评论文本")
        self.assertEqual(captured["payload"]["thinking"], {"type": "disabled"})

    def test_store_is_not_visible_until_initialization_finishes(self):
        initialization_started = threading.Event()
        allow_initialization_to_finish = threading.Event()
        second_finished = threading.Event()
        results = []

        class SlowStore:
            def __init__(self, _):
                self.initialized = False

            def initialize(self):
                initialization_started.set()
                allow_initialization_to_finish.wait(timeout=2)
                self.initialized = True

            def sync_accounts_from_config(self, *_args, **_kwargs):
                return None

            def rebase_account_directories(self, *_args, **_kwargs):
                return None

            def list_comment_libraries(self, **_kwargs):
                return []

        def read_store(finished=None):
            results.append(server.get_store().initialized)
            if finished:
                finished.set()

        original_store = server.APP_STORE
        server.APP_STORE = None
        try:
            with (
                patch.object(server, "AppStore", SlowStore),
                patch.object(server, "load_app_config", return_value={}),
                patch.object(server, "first_existing_directory", return_value=""),
            ):
                first = threading.Thread(target=read_store)
                second = threading.Thread(target=read_store, args=(second_finished,))
                first.start()
                self.assertTrue(initialization_started.wait(timeout=1))
                second.start()
                returned_before_initialization = second_finished.wait(timeout=0.05)
                allow_initialization_to_finish.set()
                first.join(timeout=1)
                second.join(timeout=1)
        finally:
            allow_initialization_to_finish.set()
            server.APP_STORE = original_store

        self.assertFalse(returned_before_initialization)
        self.assertEqual(results, [True, True])


if __name__ == "__main__":
    unittest.main()
