import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


EXECUTOR_DIR = Path(__file__).resolve().parents[2] / "src" / "Ray.BiliBiliTool.Web" / "Executor"
sys.path.insert(0, str(EXECUTOR_DIR))

import server
from app_store import AppStore


class ExcludeKeywordTests(unittest.TestCase):
    def test_filter_candidates_excludes_matches_in_title_description_or_author(self):
        candidates = [
            {
                "bvid": "BV-TITLE",
                "title": "漫步者入耳式耳机测评",
                "description": "",
                "author": "数码频道",
            },
            {
                "bvid": "BV-DESCRIPTION",
                "title": "入耳式耳机横评",
                "description": "本期包含漫步者产品",
                "author": "音频实验室",
            },
            {
                "bvid": "BV-AUTHOR",
                "title": "通勤耳机推荐",
                "description": "五款产品对比",
                "author": "漫步者官方",
            },
            {
                "bvid": "BV-CASEFOLD",
                "title": "EDIFIER NeoBuds review",
                "description": "",
                "author": "Audio Lab",
            },
            {
                "bvid": "BV-KEEP",
                "title": "入耳式耳机选购指南",
                "description": "不同价位耳机对比",
                "author": "声音研究所",
            },
        ]

        kept, excluded_count = server.filter_candidates_by_exclude_keywords(
            candidates,
            [" 漫步者 ", "edifier", "漫步者"],
        )

        self.assertEqual([item["bvid"] for item in kept], ["BV-KEEP"])
        self.assertEqual(excluded_count, 4)

    def test_filter_candidates_keeps_all_items_when_exclude_keywords_are_empty(self):
        candidates = [{"bvid": "BV-KEEP", "title": "漫步者耳机"}]

        kept, excluded_count = server.filter_candidates_by_exclude_keywords(candidates, [])

        self.assertEqual(kept, candidates)
        self.assertEqual(excluded_count, 0)

    def test_discovery_filters_excluded_candidates_before_selection_and_import(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            store = AppStore(Path(temporary_directory) / "app.db")
            store.initialize()
            selected_input = {}

            candidates = [
                {
                    "bvid": "BV1TITLE1234",
                    "url": "https://www.bilibili.com/video/BV1TITLE1234",
                    "title": "漫步者入耳式耳机",
                    "description": "",
                    "author": "数码频道",
                },
                {
                    "bvid": "BV1KEEP12345",
                    "url": "https://www.bilibili.com/video/BV1KEEP12345",
                    "title": "入耳式耳机横评",
                    "description": "多款耳机对比",
                    "author": "声音研究所",
                },
            ]

            def select_candidates(_payload, items, needed):
                selected_input["bvids"] = [item["bvid"] for item in items]
                selected = list(items[:needed])
                return {
                    "provider": "fallback",
                    "items": selected,
                    "aiSelected": 0,
                    "fallbackSelected": len(selected),
                    "fallbackReason": "",
                }

            previous_store = server.APP_STORE
            server.APP_STORE = store
            try:
                with (
                    patch.object(server, "search_bili_videos_page", return_value=candidates),
                    patch.object(
                        server,
                        "_select_candidates_with_ai_or_fallback",
                        side_effect=select_candidates,
                    ),
                ):
                    result = server.discover_videos_for_payload(
                        {
                            "keywords": ["入耳式耳机"],
                            "excludeKeywords": ["漫步者"],
                            "limit": 1,
                        },
                        dry_run=False,
                    )
            finally:
                server.APP_STORE = previous_store

            self.assertEqual(selected_input["bvids"], ["BV1KEEP12345"])
            self.assertEqual(result["excludedCandidates"], 1)
            self.assertEqual([item["bvid"] for item in store.list_videos()], ["BV1KEEP12345"])


if __name__ == "__main__":
    unittest.main()
