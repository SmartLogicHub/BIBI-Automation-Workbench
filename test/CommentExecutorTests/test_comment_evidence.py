import sqlite3
import sys
import tempfile
import unittest
from contextlib import closing
from pathlib import Path


EXECUTOR_DIR = Path(__file__).resolve().parents[2] / "src" / "Ray.BiliBiliTool.Web" / "Executor"
sys.path.insert(0, str(EXECUTOR_DIR))

from app_store import AppStore
from bili_operator import (
    STATUS_SUCCESS,
    STATUS_SUBMITTED_UNVERIFIED,
    classify_publish_confirmation,
)


class CommentEvidenceTests(unittest.TestCase):
    def test_cleared_input_without_page_confirmation_is_not_success(self):
        result = classify_publish_confirmation(
            before_text_count=0,
            after_text_count=0,
            success_hint_visible=False,
            input_cleared=True,
            page_evidence={},
            before_comment_id="",
        )

        self.assertIsNone(result)

    def test_new_visible_comment_is_platform_confirmed_with_receipt(self):
        result = classify_publish_confirmation(
            before_text_count=0,
            after_text_count=1,
            success_hint_visible=False,
            input_cleared=True,
            page_evidence={
                "found": True,
                "comment_id": "987654321",
                "comment_url": "https://www.bilibili.com/video/BV1TEST?comment_on=1&comment_root_id=987654321",
            },
            before_comment_id="",
        )

        self.assertEqual(result.status, STATUS_SUCCESS)
        self.assertEqual(result.verification_method, "comment_visible")
        self.assertEqual(result.platform_comment_id, "987654321")
        self.assertIn("BV1TEST", result.comment_url)
        self.assertTrue(result.verified_at)

    def test_explicit_platform_notice_is_confirmed_without_inventing_comment_id(self):
        result = classify_publish_confirmation(
            before_text_count=0,
            after_text_count=0,
            success_hint_visible=True,
            input_cleared=True,
            page_evidence={},
            before_comment_id="",
        )

        self.assertEqual(result.status, STATUS_SUCCESS)
        self.assertEqual(result.verification_method, "success_notice")
        self.assertEqual(result.platform_comment_id, "")

    def test_old_ledger_schema_is_migrated_and_evidence_is_persisted(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            database = Path(temporary_directory) / "app.db"
            with closing(sqlite3.connect(database)) as db:
                db.execute(
                    """
                    CREATE TABLE ledger (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        video_id INTEGER,
                        bvid TEXT NOT NULL,
                        url TEXT NOT NULL,
                        account_id INTEGER,
                        account_name TEXT NOT NULL,
                        template_text TEXT NOT NULL,
                        status TEXT NOT NULL,
                        error_reason TEXT NOT NULL DEFAULT '',
                        published_at TEXT NOT NULL DEFAULT '',
                        created_at TEXT NOT NULL
                    )
                    """
                )

            store = AppStore(database)
            store.initialize()
            with closing(sqlite3.connect(database)) as db:
                columns = {
                    row[1]
                    for row in db.execute("PRAGMA table_info(ledger)").fetchall()
                }

            self.assertTrue(
                {
                    "platform_comment_id",
                    "verification_method",
                    "proof_text",
                    "comment_url",
                    "verified_at",
                }.issubset(columns)
            )

    def test_ledger_returns_video_title_and_platform_receipt(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            store = AppStore(Path(temporary_directory) / "app.db")
            store.initialize()
            imported = store.import_videos(["BV1xx411c7mD"], source="manual", dry_run=False)
            video = store.list_videos()[0]
            with store.connect() as db:
                db.execute("UPDATE videos SET title='凭证测试视频' WHERE id=?", (video["id"],))

            store.record_publish_result(
                {
                    "video_id": video["id"],
                    "bvid": video["bvid"],
                    "url": video["url"],
                    "account_id": None,
                    "account_name": "测试账号",
                    "template_text": "这是一条可核验评论",
                },
                STATUS_SUCCESS,
                evidence={
                    "platform_comment_id": "123456",
                    "verification_method": "comment_visible",
                    "proof_text": "评论已出现在视频评论区",
                    "comment_url": "https://www.bilibili.com/video/BV1xx411c7mD?comment_on=1",
                    "verified_at": "2026-07-11T12:00:00+08:00",
                },
            )

            record = store.list_ledger()[0]

            self.assertEqual(imported["inserted"], 1)
            self.assertEqual(record["video_title"], "凭证测试视频")
            self.assertEqual(record["platform_comment_id"], "123456")
            self.assertEqual(record["verification_method"], "comment_visible")
            self.assertEqual(record["proof_text"], "评论已出现在视频评论区")

    def test_unverified_submission_is_counted_separately_from_confirmed_success(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            store = AppStore(Path(temporary_directory) / "app.db")
            store.initialize()
            store.record_publish_result(
                {
                    "video_id": None,
                    "bvid": "BV1xx411c7mD",
                    "url": "https://www.bilibili.com/video/BV1xx411c7mD",
                    "account_id": None,
                    "account_name": "测试账号",
                    "template_text": "待确认评论",
                },
                STATUS_SUBMITTED_UNVERIFIED,
                "页面未返回可核验结果",
            )

            summary = store.summary()

            self.assertEqual(summary["success_count"], 0)
            self.assertEqual(summary["pending_confirmation_count"], 1)


if __name__ == "__main__":
    unittest.main()
