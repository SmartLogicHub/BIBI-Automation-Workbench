import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class AccountConfig:
    name: str
    user_data_dir: Path
    enabled: bool = True
    daily_limit: int = 0


class AccountDirectoryLock:
    def __init__(self, user_data_dir):
        self.user_data_dir = Path(user_data_dir)
        self.lock_file = self.user_data_dir / ".executor.lock"
        self._acquired = False

    def __enter__(self):
        self.user_data_dir.mkdir(parents=True, exist_ok=True)
        try:
            handle = os.open(str(self.lock_file), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except FileExistsError as error:
            raise RuntimeError(f"user_data_dir 正在被其他任务使用：{self.user_data_dir}") from error
        with os.fdopen(handle, "w", encoding="utf-8") as file:
            file.write(str(os.getpid()))
        self._acquired = True
        return self

    def __exit__(self, exc_type, exc, tb):
        if self._acquired and self.lock_file.exists():
            self.lock_file.unlink()
        self._acquired = False


class AccountManager:
    def __init__(self, config, base_dir=None):
        self.base_dir = Path(base_dir or ".").resolve()
        self.accounts = [self._parse_account(item) for item in config.get("accounts", [])]
        if not self.accounts:
            raise ValueError("config.yaml 中至少需要配置一个账号")

    def _parse_account(self, item):
        name = str(item.get("name") or "").strip()
        if not name:
            raise ValueError("账号配置缺少 name")
        raw_dir = Path(str(item.get("user_data_dir") or f"accounts/{name}"))
        user_data_dir = raw_dir if raw_dir.is_absolute() else self.base_dir / raw_dir
        return AccountConfig(
            name=name,
            user_data_dir=user_data_dir,
            enabled=bool(item.get("enabled", True)),
            daily_limit=int(item.get("daily_limit") or 0),
        )

    def enabled_accounts(self):
        return [account for account in self.accounts if account.enabled]

    def get_account(self, name):
        for account in self.accounts:
            if account.name == name:
                if not account.enabled:
                    raise ValueError(f"账号已禁用：{name}")
                return account
        raise ValueError(f"未找到账号：{name}")
