from copy import deepcopy
from pathlib import Path

import yaml


DEFAULT_CONFIG = {
    "accounts": [],
    "task": {
        "input_file": "data/video_links.xlsx",
        "interval_seconds": 90,
        "max_retry": 2,
        "avoid_duplicate": True,
        "continue_after_manual_success": True,
        "continue_after_manual_timeout": True,
        "stop_on_verify": True,
        "stop_on_login_required": True,
    },
    "scheduler": {
        "account_interval_min_seconds": 480,
        "account_interval_max_seconds": 900,
        "same_account_cooldown_min_seconds": 1200,
        "same_account_cooldown_max_seconds": 2400,
    },
    "comment": {
        "template_file": "templates/comments.txt",
        "template_dir": "",
        "selection_mode": "random",
        "auto_submit": True,
        "human_like_submit": True,
        "human_type_min_delay_ms": 80,
        "human_type_max_delay_ms": 180,
        "human_pause_before_submit_min_ms": 1200,
        "human_pause_before_submit_max_ms": 3200,
        "human_click_delay_ms": 120,
        "wait_manual_submit": True,
        "manual_submit_timeout_seconds": 300,
        "check_submit_interval_seconds": 3,
    },
    "output": {
        "posted_file": "data/posted_records.xlsx",
        "failed_file": "data/failed_records.xlsx",
        "processed_file": "data/processed_records.xlsx",
    },
    "browser": {"headless": False, "slow_mo": 200, "prefer_system_browser": True},
}


def _deep_merge(base, override):
    result = deepcopy(base)
    for key, value in (override or {}).items():
        if isinstance(value, dict) and isinstance(result.get(key), dict):
            result[key] = _deep_merge(result[key], value)
        else:
            result[key] = value
    return result


def load_config(config_file="config.yaml"):
    path = Path(config_file)
    if not path.exists():
        return deepcopy(DEFAULT_CONFIG)
    raw = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
    return _deep_merge(DEFAULT_CONFIG, raw)
