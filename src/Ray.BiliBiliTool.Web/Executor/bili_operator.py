import random
import re
import time
from dataclasses import dataclass
from datetime import datetime
from urllib.parse import quote

from playwright.sync_api import Error as PlaywrightError
from playwright.sync_api import TimeoutError as PlaywrightTimeoutError
from playwright.sync_api import sync_playwright

from account_manager import AccountDirectoryLock

def _parse_proxy(proxy_str):
    if not proxy_str or not str(proxy_str).strip():
        return None
    proxy_str = str(proxy_str).strip()
    try:
        from urllib.parse import urlparse
        parsed = urlparse(proxy_str)
        if not parsed.scheme:
            parsed = urlparse("http://" + proxy_str)

        server = f"{parsed.scheme}://{parsed.hostname}"
        if parsed.port:
            server += f":{parsed.port}"

        ret = {"server": server}
        if parsed.username:
            ret["username"] = parsed.username
        if parsed.password:
            ret["password"] = parsed.password
        return ret
    except Exception:
        return {"server": proxy_str}

STATUS_PREPARED = "prepared_waiting_review"
STATUS_DETECTED = "manual_post_detected"
STATUS_SUCCESS = "success"
STATUS_SUBMITTED_UNVERIFIED = "submitted_unverified"
STATUS_TIMEOUT = "manual_timeout"
STATUS_LOGIN_REQUIRED = "login_required"
STATUS_VERIFY = "captcha_or_verify"
STATUS_COMMENT_CLOSED = "comment_closed"
STATUS_FAILED = "failed"


COMMENT_INPUT_SELECTORS = [
    "textarea[placeholder*='评论']",
    "textarea[placeholder*='发一条']",
    "textarea",
    "[contenteditable='true'][data-placeholder*='评论']",
    "[contenteditable='true'][placeholder*='评论']",
    ".reply-box-textarea",
    ".bili-rich-textarea__inner",
    ".comment-input [contenteditable='true']",
    ".reply-box [contenteditable='true']",
]

BILI_COMMENT_SUBMIT_BUTTON_SELECTOR = "#pub > button"

COMMENT_SUBMIT_SELECTORS = [
    BILI_COMMENT_SUBMIT_BUTTON_SELECTOR,
    "button[type='submit']",
    ".reply-box-send",
    ".reply-send",
    ".comment-submit",
    ".comment-send",
    ".bili-comment-button",
    ".bili-comment-publish",
    "[class*='submit']",
    "[class*='send']",
    "[role='button']",
    "button",
]

COMMENT_SUBMIT_KEYWORDS = ["发布", "发送", "评论"]
COMMENT_SUBMIT_ATTR_KEYWORDS = ["submit", "send", "publish", "reply"]

BILI_COMMENT_INPUT_SHADOW_CHAIN = [
    "#commentapp > bili-comments",
    "#header > bili-comments-header-renderer",
    "#commentbox > bili-comment-box",
    "#editor > bili-comment-rich-textarea",
    "#input > div > div.brt-editor",
]

BILI_COMMENT_FIXED_INPUT_SHADOW_CHAIN = [
    "#commentapp > bili-comments",
    "#header > bili-comments-header-renderer",
    "div.bili-comments-bottom-fixed-wrapper > div > bili-comment-box",
    "#editor > bili-comment-rich-textarea",
    "#input > div > div.brt-editor",
]

BILI_COMMENT_SUBMIT_BOX_SHADOW_CHAIN = [
    "#commentapp > bili-comments",
    "#header > bili-comments-header-renderer",
    "#commentbox > bili-comment-box",
]

BILI_COMMENT_FIXED_SUBMIT_BOX_SHADOW_CHAIN = [
    "#commentapp > bili-comments",
    "#header > bili-comments-header-renderer",
    "div.bili-comments-bottom-fixed-wrapper > div > bili-comment-box",
]

BILI_COMMENT_INPUT_SHADOW_CHAINS = [
    BILI_COMMENT_INPUT_SHADOW_CHAIN,
    BILI_COMMENT_FIXED_INPUT_SHADOW_CHAIN,
]

BILI_COMMENT_SUBMIT_BOX_SHADOW_CHAINS = [
    BILI_COMMENT_SUBMIT_BOX_SHADOW_CHAIN,
    BILI_COMMENT_FIXED_SUBMIT_BOX_SHADOW_CHAIN,
]

VERIFY_KEYWORDS = ["验证码", "安全验证", "人机验证", "请完成验证", "访问异常", "账号异常", "风险"]
LOGIN_KEYWORDS = ["登录后发表评论", "请先登录", "登录后才能评论", "未登录"]
COMMENT_CLOSED_KEYWORDS = ["评论区已关闭", "已关闭评论", "无法评论", "评论已关闭"]
SUCCESS_HINT_KEYWORDS = ["评论成功", "发布成功", "发送成功", "评论已发布"]
FAILURE_HINT_KEYWORDS = ["评论失败", "发布失败", "发送失败", "请稍后再试", "过于频繁", "内容违规", "包含敏感", "风控"]
SEARCH_BLOCKING_KEYWORDS = ["验证码", "安全验证", "人机验证", "访问异常", "请求过于频繁", "风控", "请完成验证"]

DEFAULT_HUMAN_TYPE_MIN_DELAY_MS = 80
DEFAULT_HUMAN_TYPE_MAX_DELAY_MS = 180
DEFAULT_HUMAN_PAUSE_BEFORE_SUBMIT_MIN_MS = 1200
DEFAULT_HUMAN_PAUSE_BEFORE_SUBMIT_MAX_MS = 3200
DEFAULT_HUMAN_CLICK_DELAY_MS = 120


@dataclass(frozen=True)
class OperatorResult:
    status: str
    error_reason: str = ""
    verification_method: str = ""
    proof_text: str = ""
    platform_comment_id: str = ""
    comment_url: str = ""
    verified_at: str = ""

    def evidence(self):
        return {
            "verification_method": self.verification_method,
            "proof_text": self.proof_text,
            "platform_comment_id": self.platform_comment_id,
            "comment_url": self.comment_url,
            "verified_at": self.verified_at,
        }


def is_manual_submit_detected(before_text_count, after_text_count, success_hint_visible, input_cleared):
    if after_text_count > before_text_count:
        return True
    return bool(success_hint_visible and input_cleared)


def classify_publish_confirmation(
    before_text_count,
    after_text_count,
    success_hint_visible,
    input_cleared,
    page_evidence=None,
    before_comment_id="",
):
    evidence = page_evidence if isinstance(page_evidence, dict) else {}
    comment_id = str(evidence.get("comment_id") or "").strip()
    comment_url = str(evidence.get("comment_url") or "").strip()
    new_comment_id = bool(comment_id and comment_id != str(before_comment_id or "").strip())
    comment_became_visible = after_text_count > before_text_count
    if comment_became_visible or (evidence.get("found") and new_comment_id):
        return OperatorResult(
            STATUS_SUCCESS,
            verification_method="comment_visible",
            proof_text="评论已出现在视频评论区",
            platform_comment_id=comment_id,
            comment_url=comment_url,
            verified_at=datetime.now().astimezone().isoformat(timespec="seconds"),
        )
    if success_hint_visible and input_cleared:
        return OperatorResult(
            STATUS_SUCCESS,
            verification_method="success_notice",
            proof_text="页面已明确提示评论发布成功",
            verified_at=datetime.now().astimezone().isoformat(timespec="seconds"),
        )
    return None


def launch_persistent_browser_context(playwright, user_data_dir, browser_config=None, **overrides):
    browser_config = browser_config or {}
    launch_options = {
        "user_data_dir": str(user_data_dir),
        "headless": bool(overrides.get("headless", browser_config.get("headless", False))),
        "slow_mo": int(overrides.get("slow_mo", browser_config.get("slow_mo") or 0) or 0),
        "viewport": overrides.get("viewport") or {"width": 1366, "height": 900},
    }
    if overrides.get("proxy"):
        launch_options["proxy"] = overrides["proxy"]
    configured_channel = str(browser_config.get("channel") or "").strip()
    channels = []
    if configured_channel:
        channels.append(configured_channel)
    if browser_config.get("prefer_system_browser", True):
        channels.extend(["chrome", "msedge"])

    last_error = None
    for channel in dict.fromkeys(channels):
        try:
            return playwright.chromium.launch_persistent_context(**launch_options, channel=channel)
        except PlaywrightError as error:
            last_error = error

    try:
        return playwright.chromium.launch_persistent_context(**launch_options)
    except PlaywrightError as error:
        if last_error:
            raise PlaywrightError(
                "无法启动浏览器。请安装 Chrome/Edge，或在当前电脑执行 playwright install chromium。"
            ) from error
        raise


class BiliOperator:
    def __init__(self, browser_config=None, comment_config=None, logger=None):
        self.browser_config = browser_config or {}
        self.comment_config = comment_config or {}
        self.logger = logger

    def search_videos_with_browser(self, account, keywords, per_keyword=8, year="", max_pages=2):
        results = []
        seen = set()
        proxy_config = None
        if hasattr(account, "proxy") and account.proxy:
            proxy_config = _parse_proxy(account.proxy)

        with AccountDirectoryLock(account.user_data_dir):
            with sync_playwright() as playwright:
                context = launch_persistent_browser_context(playwright, account.user_data_dir, self.browser_config, proxy=proxy_config)
                try:
                    page = context.pages[0] if context.pages else context.new_page()
                    for keyword in list(keywords or [])[:6]:
                        keyword_added = 0
                        for page_number in range(1, max(1, int(max_pages or 1)) + 1):
                            if keyword_added >= per_keyword:
                                break
                            search_url = (
                                "https://search.bilibili.com/all"
                                f"?keyword={quote(str(keyword))}&page={page_number}&order=pubdate&from_source=webtop_search"
                            )
                            page.goto(search_url, wait_until="domcontentloaded", timeout=45000)
                            try:
                                page.wait_for_load_state("networkidle", timeout=12000)
                            except PlaywrightTimeoutError:
                                pass
                            page.wait_for_timeout(random.randint(900, 1800))
                            for _ in range(2):
                                page.mouse.wheel(0, random.randint(500, 900))
                                page.wait_for_timeout(random.randint(450, 900))
                            body_text = self._safe_body_text(page)
                            if any(blocking in body_text for blocking in SEARCH_BLOCKING_KEYWORDS):
                                raise PlaywrightError("B站搜索页出现验证码、安全验证或访问异常提示")
                            for item in self._extract_search_result_links(page, keyword, page_number):
                                bvid = item.get("bvid") or ""
                                if not bvid or bvid in seen:
                                    continue
                                seen.add(bvid)
                                keyword_added += 1
                                results.append(item)
                                if keyword_added >= per_keyword:
                                    break
                finally:
                    context.close()
        return results

    def _extract_search_result_links(self, page, keyword, page_number):
        links = page.evaluate(
            """() => Array.from(document.querySelectorAll('a[href*="/video/BV"]')).map((anchor) => ({
                href: anchor.href || anchor.getAttribute('href') || '',
                title: anchor.getAttribute('title') || anchor.innerText || anchor.textContent || ''
            })).slice(0, 80)"""
        )
        items = []
        seen = set()
        for link in links or []:
            href = str(link.get("href") or "")
            match = re.search(r"BV[a-zA-Z0-9]+", href)
            if not match:
                continue
            bvid = match.group(0)
            if bvid in seen:
                continue
            seen.add(bvid)
            title = re.sub(r"\s+", " ", str(link.get("title") or keyword)).strip()
            items.append(
                {
                    "bvid": bvid,
                    "url": f"https://www.bilibili.com/video/{bvid}",
                    "title": title or str(keyword),
                    "author": "",
                    "description": "",
                    "category": "",
                    "keyword": str(keyword),
                    "play": None,
                    "favorites": None,
                    "pubdate": None,
                    "year": None,
                    "search_page": page_number,
                    "source": "account_browser",
                }
            )
        return items

    def publish_comment(self, account, video_url, comment_text, on_prepared=None):
        proxy_config = None
        if hasattr(account, "proxy") and account.proxy:
            proxy_config = _parse_proxy(account.proxy)

        with AccountDirectoryLock(account.user_data_dir):
            with sync_playwright() as playwright:
                context = launch_persistent_browser_context(playwright, account.user_data_dir, self.browser_config, proxy=proxy_config)
                try:
                    page = context.pages[0] if context.pages else context.new_page()
                    return self._publish_comment_on_page(page, video_url, comment_text, on_prepared=on_prepared)
                finally:
                    context.close()

    def _publish_comment_on_page(self, page, video_url, comment_text, on_prepared=None):
        try:
            page.goto(video_url, wait_until="domcontentloaded", timeout=45000)
            try:
                page.wait_for_load_state("networkidle", timeout=15000)
            except PlaywrightTimeoutError:
                pass

            blocking_status = self._detect_blocking_status(page)
            if blocking_status:
                return blocking_status

            self._scroll_to_comment_area(page)
            blocking_status = self._detect_blocking_status(page)
            if blocking_status:
                return blocking_status

            before_count = self._count_text_outside_inputs(page, comment_text)
            before_evidence = self._find_comment_evidence(page, comment_text)
            input_locator = self._find_comment_input(page)
            if input_locator is None:
                return OperatorResult(STATUS_COMMENT_CLOSED, "没有找到可用的评论输入框")

            self._fill_comment_input(page, input_locator, comment_text)
            if on_prepared:
                on_prepared()
            auto_submitted = self._config_enabled("auto_submit")
            submit_result = self._auto_click_submit_if_enabled(page)
            if submit_result:
                return submit_result
            return self._wait_for_publish_result(
                page,
                input_locator,
                comment_text,
                before_count,
                auto_submitted=auto_submitted,
                before_comment_id=before_evidence.get("comment_id") if before_evidence else "",
            )
        except PlaywrightTimeoutError as error:
            return OperatorResult(STATUS_FAILED, f"页面加载或等待超时：{error}")
        except PlaywrightError as error:
            return OperatorResult(STATUS_FAILED, f"浏览器自动化失败：{error}")
        except Exception as error:
            return OperatorResult(STATUS_FAILED, str(error))

    def _detect_blocking_status(self, page):
        text = self._safe_body_text(page)
        if any(keyword in text for keyword in VERIFY_KEYWORDS):
            return OperatorResult(STATUS_VERIFY, "页面出现验证码或安全验证提示")
        if any(keyword in text for keyword in COMMENT_CLOSED_KEYWORDS):
            return OperatorResult(STATUS_COMMENT_CLOSED, "评论区关闭或无法评论")
        if any(keyword in text for keyword in LOGIN_KEYWORDS):
            return OperatorResult(STATUS_LOGIN_REQUIRED, "登录失效或需要登录后评论")
        return None

    def _scroll_to_comment_area(self, page):
        for _ in range(5):
            page.mouse.wheel(0, 900)
            page.wait_for_timeout(500)

    def _find_comment_input(self, page):
        shadow_locator = self._find_bili_shadow_comment_input(page)
        if shadow_locator is not None:
            return shadow_locator
        for selector in COMMENT_INPUT_SELECTORS:
            locator = page.locator(selector).first
            try:
                if locator.count() and locator.is_visible(timeout=1000):
                    return locator
            except PlaywrightError:
                continue
        return None

    def _auto_click_submit_if_enabled(self, page):
        if not self._config_enabled("auto_submit"):
            return None
        submit_button = self._find_comment_submit_button(page)
        if submit_button is None:
            return OperatorResult(STATUS_FAILED, "没有找到可点击的评论提交按钮")
        if self._config_enabled("human_like_submit"):
            self._wait_random_ms(
                page,
                "human_pause_before_submit_min_ms",
                "human_pause_before_submit_max_ms",
                DEFAULT_HUMAN_PAUSE_BEFORE_SUBMIT_MIN_MS,
                DEFAULT_HUMAN_PAUSE_BEFORE_SUBMIT_MAX_MS,
            )
        click_delay = self._config_int("human_click_delay_ms", DEFAULT_HUMAN_CLICK_DELAY_MS)
        try:
            submit_button.click(timeout=5000, delay=click_delay)
            return None
        except TypeError:
            submit_button.click()
            return None
        except PlaywrightError as error:
            return OperatorResult(STATUS_FAILED, f"点击评论提交按钮失败：{error}")

    def _config_enabled(self, key):
        value = self.comment_config.get(key)
        if isinstance(value, str):
            return value.strip().lower() in {"1", "true", "yes", "on", "y", "是", "开启"}
        return bool(value)

    def _config_int(self, key, default):
        try:
            return int(self.comment_config.get(key, default))
        except (TypeError, ValueError):
            return default

    def _wait_random_ms(self, page, min_key, max_key, default_min, default_max):
        min_ms = max(0, self._config_int(min_key, default_min))
        max_ms = max(min_ms, self._config_int(max_key, default_max))
        delay_ms = min_ms if min_ms == max_ms else random.randint(min_ms, max_ms)
        if delay_ms:
            page.wait_for_timeout(delay_ms)
        return delay_ms

    def _find_comment_submit_button(self, page):
        shadow_button = self._find_bili_shadow_comment_submit_button(page)
        if shadow_button is not None:
            return shadow_button
        for selector in COMMENT_SUBMIT_SELECTORS:
            try:
                candidates = page.locator(selector)
                count = min(candidates.count(), 30)
            except PlaywrightError:
                continue
            for index in range(count):
                locator = candidates.nth(index)
                try:
                    if locator.is_visible(timeout=500) and self._element_looks_like_submit(locator):
                        return locator
                except PlaywrightError:
                    continue
        return None

    def _find_bili_shadow_comment_submit_button(self, page):
        for chain in BILI_COMMENT_SUBMIT_BOX_SHADOW_CHAINS:
            element = self._resolve_shadow_descendant(page, chain, COMMENT_SUBMIT_SELECTORS)
            if element is not None:
                return element
        return None

    def _resolve_shadow_descendant(self, page, root_chain, descendant_selectors):
        handle = page.evaluate_handle(
            """payload => {
                const textKeywords = payload.textKeywords;
                const attrKeywords = payload.attrKeywords;

                const visible = element => {
                    const rect = element.getBoundingClientRect();
                    const style = window.getComputedStyle(element);
                    return rect.width > 0 &&
                        rect.height > 0 &&
                        style.visibility !== 'hidden' &&
                        style.display !== 'none';
                };

                const enabled = element => {
                    return !element.disabled &&
                        element.getAttribute('aria-disabled') !== 'true' &&
                        !element.classList.contains('disabled');
                };

                const haystack = element => [
                    element.innerText,
                    element.textContent,
                    element.getAttribute('aria-label'),
                    element.getAttribute('title'),
                    element.getAttribute('value'),
                    element.id,
                    element.className,
                ].filter(Boolean).join(' ').toLowerCase();

                const looksLikeSubmit = element => {
                    const text = haystack(element);
                    return textKeywords.some(keyword => text.includes(keyword)) ||
                        attrKeywords.some(keyword => text.includes(keyword));
                };

                let root = document;
                for (const selector of payload.rootChain) {
                    const node = root.querySelector(selector);
                    if (!node) return null;
                    root = node.shadowRoot || node;
                }

                for (const selector of payload.descendantSelectors) {
                    for (const candidate of root.querySelectorAll(selector)) {
                        if (visible(candidate) && enabled(candidate) && looksLikeSubmit(candidate)) {
                            return candidate;
                        }
                    }
                }
                return null;
            }""",
            {
                "rootChain": root_chain,
                "descendantSelectors": descendant_selectors,
                "textKeywords": COMMENT_SUBMIT_KEYWORDS,
                "attrKeywords": COMMENT_SUBMIT_ATTR_KEYWORDS,
            },
        )
        try:
            element = handle.as_element()
            if not element:
                handle.dispose()
                return None
            return element
        except PlaywrightError:
            try:
                handle.dispose()
            except PlaywrightError:
                pass
            return None

    def _element_looks_like_submit(self, locator):
        return bool(
            locator.evaluate(
                """(element, payload) => {
                    const haystack = [
                        element.innerText,
                        element.textContent,
                        element.getAttribute('aria-label'),
                        element.getAttribute('title'),
                        element.getAttribute('value'),
                        element.id,
                        element.className,
                    ].filter(Boolean).join(' ').toLowerCase();
                    return payload.textKeywords.some(keyword => haystack.includes(keyword)) ||
                        payload.attrKeywords.some(keyword => haystack.includes(keyword));
                }""",
                {
                    "textKeywords": COMMENT_SUBMIT_KEYWORDS,
                    "attrKeywords": COMMENT_SUBMIT_ATTR_KEYWORDS,
                },
            )
        )

    def _find_bili_shadow_comment_input(self, page):
        for chain in BILI_COMMENT_INPUT_SHADOW_CHAINS:
            element = self._resolve_shadow_chain(page, chain)
            if element is not None:
                return element
        return None

    def _resolve_shadow_chain(self, page, chain):
        handle = page.evaluate_handle(
            """chain => {
                let root = document;
                for (const selector of chain) {
                    const node = root.querySelector(selector);
                    if (!node) return null;
                    root = node.shadowRoot || node;
                }
                return root;
            }""",
            chain,
        )
        try:
            element = handle.as_element()
            if not element:
                handle.dispose()
                return None
            visible = element.evaluate(
                """node => {
                    const rect = node.getBoundingClientRect();
                    const style = window.getComputedStyle(node);
                    return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
                }"""
            )
            if not visible:
                element.dispose()
                return None
            return element
        except PlaywrightError:
            try:
                handle.dispose()
            except PlaywrightError:
                pass
            return None

    def _fill_comment_input(self, page, locator, comment_text):
        if self._config_enabled("human_like_submit"):
            self._fill_comment_input_human_like(page, locator, comment_text)
            return
        try:
            locator.fill(comment_text, timeout=3000)
            return
        except (AttributeError, PlaywrightError):
            pass
        locator.click(timeout=3000)
        page.keyboard.press("Control+A")
        page.keyboard.insert_text(comment_text)

    def _fill_comment_input_human_like(self, page, locator, comment_text):
        locator.click(timeout=3000)
        page.keyboard.press("Control+A")
        for index, character in enumerate(comment_text):
            page.keyboard.insert_text(character)
            if index < len(comment_text) - 1:
                self._wait_random_ms(
                    page,
                    "human_type_min_delay_ms",
                    "human_type_max_delay_ms",
                    DEFAULT_HUMAN_TYPE_MIN_DELAY_MS,
                    DEFAULT_HUMAN_TYPE_MAX_DELAY_MS,
                )

    def _wait_for_publish_result(
        self,
        page,
        input_locator,
        comment_text,
        before_count,
        auto_submitted=False,
        before_comment_id="",
    ):
        timeout_seconds = int(self.comment_config.get("manual_submit_timeout_seconds") or 300)
        interval_seconds = int(self.comment_config.get("check_submit_interval_seconds") or 3)
        verification_grace_seconds = max(
            interval_seconds,
            int(self.comment_config.get("verification_grace_seconds") or 12),
        )
        deadline = time.monotonic() + timeout_seconds
        input_cleared_at = None

        while time.monotonic() < deadline:
            blocking_status = self._detect_blocking_status(page)
            if blocking_status and blocking_status.status in [STATUS_LOGIN_REQUIRED, STATUS_VERIFY, STATUS_COMMENT_CLOSED]:
                return blocking_status

            if self._failure_hint_visible(page):
                return OperatorResult(STATUS_FAILED, "页面提示评论发布失败，请检查账号状态或评论内容")

            after_count = self._count_text_outside_inputs(page, comment_text)
            success_hint_visible = self._success_hint_visible(page)
            input_cleared = self._input_cleared(input_locator)
            page_evidence = self._find_comment_evidence(page, comment_text)
            if page_evidence.get("found") and not page_evidence.get("comment_url"):
                page_evidence["comment_url"] = str(getattr(page, "url", "") or "")
            confirmation = classify_publish_confirmation(
                before_count,
                after_count,
                success_hint_visible,
                input_cleared,
                page_evidence,
                before_comment_id,
            )
            if confirmation:
                return confirmation

            if input_cleared:
                input_cleared_at = input_cleared_at or time.monotonic()
                if time.monotonic() - input_cleared_at >= verification_grace_seconds:
                    return OperatorResult(
                        STATUS_SUBMITTED_UNVERIFIED,
                        "评论已提交，但页面没有返回可核验结果",
                        verification_method="submitted_unverified",
                        proof_text="需要打开视频评论区人工确认",
                        comment_url=str(getattr(page, "url", "") or ""),
                    )
            else:
                input_cleared_at = None
            page.wait_for_timeout(interval_seconds * 1000)

        if input_cleared_at is not None or (auto_submitted and self._input_cleared(input_locator)):
            return OperatorResult(
                STATUS_SUBMITTED_UNVERIFIED,
                "评论已提交，但页面没有返回可核验结果",
                verification_method="submitted_unverified",
                proof_text="需要打开视频评论区人工确认",
                comment_url=str(getattr(page, "url", "") or ""),
            )
        return OperatorResult(STATUS_TIMEOUT, f"等待发布结果超过 {timeout_seconds} 秒")

    def _success_hint_visible(self, page):
        text = self._page_text(page)
        return any(keyword in text for keyword in SUCCESS_HINT_KEYWORDS)

    def _failure_hint_visible(self, page):
        text = self._page_text(page)
        return any(keyword in text for keyword in FAILURE_HINT_KEYWORDS)

    def _input_cleared(self, locator):
        try:
            value = locator.evaluate(
                """element => {
                    if ('value' in element) return element.value || '';
                    return element.innerText || element.textContent || '';
                }"""
            )
            return not str(value or "").strip()
        except PlaywrightError:
            return False

    def _count_text_outside_inputs(self, page, text):
        if not text:
            return 0
        haystack = self._page_text(page, exclude_inputs=True)
        return haystack.count(text)

    def _find_comment_evidence(self, page, text):
        if not str(text or "").strip():
            return {}
        try:
            result = page.evaluate(
                """payload => {
                    const expected = String(payload.text || '').replace(/\s+/g, ' ').trim();
                    if (!expected) return {};
                    const roots = [document];
                    const elements = [];
                    for (let index = 0; index < roots.length; index += 1) {
                        const root = roots[index];
                        for (const element of root.querySelectorAll('*')) {
                            elements.push(element);
                            if (element.shadowRoot) roots.push(element.shadowRoot);
                        }
                    }
                    const candidates = elements.filter(element => {
                        if (element.closest?.('textarea,input,select,[contenteditable="true"]')) return false;
                        const value = String(element.innerText || element.textContent || '')
                            .replace(/\s+/g, ' ')
                            .trim();
                        return value.includes(expected);
                    }).sort((left, right) => {
                        const leftLength = String(left.innerText || left.textContent || '').length;
                        const rightLength = String(right.innerText || right.textContent || '').length;
                        return leftLength - rightLength;
                    });
                    if (!candidates.length) return {};

                    const parentElement = element => {
                        if (element.parentElement) return element.parentElement;
                        const root = element.getRootNode?.();
                        return root?.host || null;
                    };
                    let current = candidates[0];
                    let commentId = '';
                    let commentUrl = '';
                    for (let depth = 0; current && depth < 10; depth += 1) {
                        commentId = commentId
                            || current.getAttribute?.('data-rpid')
                            || current.getAttribute?.('data-reply-id')
                            || current.getAttribute?.('reply-id')
                            || current.getAttribute?.('rpid')
                            || '';
                        const link = current.matches?.('a')
                            ? current
                            : current.querySelector?.('a[href*="comment_root_id"],a[href*="comment_on"],a[href*="reply"]');
                        if (link?.href) commentUrl = link.href;
                        if (commentId && commentUrl) break;
                        current = parentElement(current);
                    }
                    return {
                        found: true,
                        comment_id: String(commentId || ''),
                        comment_url: String(commentUrl || '')
                    };
                }""",
                {"text": str(text)},
            )
            return result if isinstance(result, dict) else {}
        except PlaywrightError:
            return {}

    def _page_text(self, page, exclude_inputs=False):
        try:
            return str(
                page.evaluate(
                    """payload => {
                        const pieces = [];
                        const shouldSkip = element => {
                            if (!payload.excludeInputs || !element.matches) return false;
                            return element.matches('textarea,input,select,[contenteditable="true"]') ||
                                element.closest('textarea,input,select,[contenteditable="true"]');
                        };
                        const walk = node => {
                            if (!node) return;
                            if (node.nodeType === Node.TEXT_NODE) {
                                const value = (node.textContent || '').trim();
                                if (value) pieces.push(value);
                                return;
                            }
                            if (node.nodeType !== Node.ELEMENT_NODE && node.nodeType !== Node.DOCUMENT_FRAGMENT_NODE) {
                                return;
                            }
                            if (node.nodeType === Node.ELEMENT_NODE && shouldSkip(node)) {
                                return;
                            }
                            if (node.shadowRoot) {
                                walk(node.shadowRoot);
                            }
                            for (const child of node.childNodes || []) {
                                walk(child);
                            }
                        };
                        walk(document.body);
                        return pieces.join('\\n');
                    }""",
                    {"excludeInputs": bool(exclude_inputs)},
                )
                or ""
            )
        except PlaywrightError:
            return self._safe_body_text(page)

    def _safe_body_text(self, page):
        try:
            return str(page.locator("body").inner_text(timeout=3000) or "")
        except PlaywrightError:
            return ""
