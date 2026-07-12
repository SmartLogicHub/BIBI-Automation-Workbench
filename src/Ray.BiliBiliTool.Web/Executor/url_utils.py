import re


BVID_PATTERN = re.compile(r"BV[a-zA-Z0-9]+")


def extract_bvid(value):
    match = BVID_PATTERN.search(str(value or ""))
    return match.group(0) if match else ""


def canonical_bili_url(value):
    bvid = extract_bvid(value)
    return f"https://www.bilibili.com/video/{bvid}" if bvid else ""
