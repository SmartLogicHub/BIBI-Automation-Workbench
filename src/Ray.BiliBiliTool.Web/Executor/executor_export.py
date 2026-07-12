from pathlib import Path

from openpyxl import Workbook


HEADERS = ["video_url", "title", "source_keyword", "created_at"]


def write_video_links_excel(items, output_file):
    path = Path(output_file)
    path.parent.mkdir(parents=True, exist_ok=True)

    workbook = Workbook()
    sheet = workbook.active
    sheet.append(HEADERS)

    seen = set()
    written = 0
    for item in items or []:
        video_url = str(item.get("video_url") or item.get("url") or "").strip()
        if not video_url or video_url in seen:
            continue
        seen.add(video_url)
        sheet.append([
            video_url,
            str(item.get("title") or "").strip(),
            str(item.get("source_keyword") or item.get("keywords") or "").strip(),
            str(item.get("created_at") or item.get("createdAt") or "").strip(),
        ])
        written += 1

    workbook.save(path)
    workbook.close()
    return written
