import sys
import tempfile
import threading
import unittest
import json
from pathlib import Path
from unittest.mock import patch
from urllib.request import Request, urlopen


EXECUTOR_DIR = Path(__file__).resolve().parents[2] / "src" / "Ray.BiliBiliTool.Web" / "Executor"
sys.path.insert(0, str(EXECUTOR_DIR))

from app_store import AppStore
from job_manager import Job, JobManager
import server


class AccountTargetTests(unittest.TestCase):
    def post_json(self, handler_type, path, payload=None):
        httpd = server.ExclusiveThreadingHTTPServer(("127.0.0.1", 0), handler_type)
        thread = threading.Thread(target=httpd.serve_forever, daemon=True)
        thread.start()
        try:
            body = json.dumps(payload or {}).encode("utf-8")
            request = Request(
                f"http://127.0.0.1:{httpd.server_port}{path}",
                data=body,
                headers={"Content-Type": "application/json"},
                method="POST",
            )
            with urlopen(request, timeout=5) as response:
                return json.loads(response.read().decode("utf-8"))
        finally:
            httpd.shutdown()
            httpd.server_close()
            thread.join(timeout=5)

    def create_store(self, root):
        store = AppStore(root / "app.db")
        store.initialize()
        first = store.create_account({"name": "账号一"}, base_dir=root)
        second = store.create_account({"name": "账号二"}, base_dir=root)
        first = store.update_account(first["id"], {"uid": "10001", "loginStatus": "ok"})
        second = store.update_account(second["id"], {"uid": "10002", "loginStatus": "ok"})
        return store, first, second

    def test_resolves_comment_account_by_local_id(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            store, first, _ = self.create_store(Path(temporary_directory))

            resolved = server.resolve_account_target(store, account_id=str(first["id"]))

            self.assertEqual(resolved["id"], first["id"])
            self.assertEqual(resolved["uid"], "10001")

    def test_resolves_workflow_account_by_platform_uid(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            store, _, second = self.create_store(Path(temporary_directory))

            resolved = server.resolve_account_target(store, account_uid="10002")

            self.assertEqual(resolved["id"], second["id"])

    def test_rejects_mismatched_local_id_and_platform_uid(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            store, first, second = self.create_store(Path(temporary_directory))

            with self.assertRaisesRegex(server.JobError, "不属于同一个账号"):
                server.resolve_account_target(
                    store,
                    account_id=str(first["id"]),
                    account_uid=second["uid"],
                )

    def test_rejects_missing_or_deleted_account(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            store, first, _ = self.create_store(Path(temporary_directory))
            store.delete_account(first["id"])

            with self.assertRaisesRegex(server.JobError, "未找到对应账号"):
                server.resolve_account_target(store, account_id=str(first["id"]))

    def test_allows_only_https_bilibili_targets(self):
        allowed = [
            "https://www.bilibili.com/video/BV1xx411c7mD",
            "https://space.bilibili.com/10001",
            "https://live.bilibili.com/1",
            "https://b23.tv/example",
        ]
        rejected = [
            "http://www.bilibili.com/video/BV1xx411c7mD",
            "https://bilibili.com.evil.example/video/BV1xx411c7mD",
            "https://example.com/",
            "javascript:alert(1)",
            "",
        ]

        for target in allowed:
            with self.subTest(target=target):
                self.assertEqual(server.validate_bilibili_target_url(target), target)
        for target in rejected:
            with self.subTest(target=target):
                with self.assertRaisesRegex(server.JobError, "B 站 HTTPS"):
                    server.validate_bilibili_target_url(target)

    def test_stop_all_requests_only_active_jobs_and_is_idempotent(self):
        manager = JobManager()
        active = Job(id="active", action="comment", status="running")
        queued = Job(id="queued", action="search", status="started")
        completed = Job(id="done", action="comment", status="done")
        manager._save(active)
        manager._save(queued)
        manager._save(completed)

        self.assertEqual(manager.request_stop_all(), 2)
        self.assertTrue(active.stop_requested)
        self.assertTrue(queued.stop_requested)
        self.assertFalse(completed.stop_requested)
        self.assertEqual(manager.request_stop_all(), 0)

    def test_open_action_uses_resolved_account_and_validated_target(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            store, first, _ = self.create_store(Path(temporary_directory))
            previous_store = server.APP_STORE
            server.APP_STORE = store
            job = Job(id="open-target", action="open_account_target", status="running")
            try:
                with patch.object(
                    server,
                    "open_account_target_browser",
                    return_value={"opened": True, "located": True},
                ) as open_browser:
                    result = server.action_open_account_target(
                        job,
                        {
                            "accountId": str(first["id"]),
                            "targetUrl": "https://www.bilibili.com/video/BV1xx411c7mD",
                            "targetKind": "comment",
                            "targetId": "98765",
                            "targetText": "测试评论",
                        },
                    )
            finally:
                server.APP_STORE = previous_store

            self.assertTrue(result["opened"])
            arguments = open_browser.call_args.args
            self.assertIs(arguments[0], job)
            self.assertEqual(arguments[1]["id"], first["id"])
            self.assertEqual(arguments[2], "https://www.bilibili.com/video/BV1xx411c7mD")
            self.assertEqual(open_browser.call_args.kwargs["target_id"], "98765")
            self.assertEqual(open_browser.call_args.kwargs["target_text"], "测试评论")

    def test_open_action_reports_busy_account_without_falling_back(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            store, first, _ = self.create_store(Path(temporary_directory))
            previous_store = server.APP_STORE
            server.APP_STORE = store
            job = Job(id="open-target", action="open_account_target", status="running")
            try:
                with patch.object(
                    server,
                    "open_account_target_browser",
                    side_effect=RuntimeError("user_data_dir 正在被其他任务使用"),
                ):
                    with self.assertRaisesRegex(server.JobError, "正在执行任务"):
                        server.action_open_account_target(
                            job,
                            {
                                "accountId": str(first["id"]),
                                "targetUrl": "https://www.bilibili.com/video/BV1xx411c7mD",
                            },
                        )
            finally:
                server.APP_STORE = previous_store

    def test_browser_session_closes_context_when_stop_is_requested(self):
        class FakePage:
            def __init__(self, job):
                self.job = job
                self.goto_url = ""

            def goto(self, url, **_):
                self.goto_url = url

            def wait_for_timeout(self, _):
                self.job.stop_requested = True

            def evaluate(self, *_):
                return True

        class FakeContext:
            def __init__(self, page):
                self.pages = [page]
                self.closed = False

            def close(self):
                self.closed = True
                self.pages = []

        class FakePlaywrightManager:
            def __enter__(self):
                return object()

            def __exit__(self, *_):
                return False

        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            account = {
                "id": 1,
                "name": "账号一",
                "user_data_dir": str(root / "profile"),
                "proxy": "",
            }
            job = Job(id="viewer", action="open_account_target", status="running")
            page = FakePage(job)
            context = FakeContext(page)

            result = server.open_account_target_browser(
                job,
                account,
                "https://www.bilibili.com/video/BV1xx411c7mD",
                target_kind="comment",
                target_id="98765",
                target_text="测试评论",
                browser_config={},
                playwright_factory=lambda: FakePlaywrightManager(),
                context_launcher=lambda *_args, **_kwargs: context,
            )

            self.assertTrue(result["opened"])
            self.assertTrue(result["located"])
            self.assertEqual(page.goto_url, "https://www.bilibili.com/video/BV1xx411c7mD")
            self.assertTrue(context.closed)

    def test_http_route_starts_open_account_target_action(self):
        class RecordingHandler(server.Handler):
            observed = None

            def start_action(self, action_id, worker):
                type(self).observed = (action_id, worker)
                self.send_json({"jobId": "job-test", "status": "started"})

        response = self.post_json(
            RecordingHandler,
            "/api/actions/open-account-target",
            {"accountId": "1", "targetUrl": "https://www.bilibili.com/"},
        )

        self.assertEqual(response["jobId"], "job-test")
        self.assertEqual(RecordingHandler.observed, ("open_account_target", server.action_open_account_target))

    def test_http_route_stops_all_active_jobs(self):
        previous_jobs = server.JOBS
        manager = JobManager()
        manager._save(Job(id="running", action="comment", status="running"))
        manager._save(Job(id="done", action="comment", status="done"))
        server.JOBS = manager
        try:
            response = self.post_json(server.Handler, "/api/jobs/stop-all")
        finally:
            server.JOBS = previous_jobs

        self.assertTrue(response["ok"])
        self.assertEqual(response["stoppedCount"], 1)


if __name__ == "__main__":
    unittest.main()
