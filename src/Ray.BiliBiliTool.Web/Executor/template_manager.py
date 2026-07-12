import random
from pathlib import Path


class CommentTemplateManager:
    def __init__(self, template_file, selection_mode="random"):
        self.template_file = Path(template_file)
        self.selection_mode = selection_mode or "random"
        self._index = 0
        self.templates = self._load_templates()

    def _load_templates(self):
        if not self.template_file.exists():
            raise FileNotFoundError(f"评价模板文件不存在：{self.template_file}")
        templates = [
            line.strip()
            for line in self.template_file.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
        if not templates:
            raise ValueError("评价模板文件为空，请至少提供一条评论模板")
        return templates

    def choose(self):
        if self.selection_mode == "sequential":
            value = self.templates[self._index % len(self.templates)]
            self._index += 1
            return value
        return random.choice(self.templates)
