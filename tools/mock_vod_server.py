"""本地演示接口（仅用于生成 README 截图）。

提供一个最小可用的苹果CMS / MacCMS V10 JSON 接口：
    GET /api.php/provide/vod?ac=detail&pg=1[&t=<type_id>][&wd=<关键词>]
    GET /api.php/provide/vod?ac=detail&ids=<id>
    GET /api.php/provide/vod?ac=list
    GET /posters/NNN.jpg

所有片名、海报都是本地生成的虚构内容，不连接任何真实资源站。
"""

import json
import os
import random
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

HERE = os.path.dirname(os.path.abspath(__file__))
POSTERS = os.path.join(HERE, "posters")

# 端口可用 PORT 环境变量覆盖：起第二个实例即可验证「多源聚合搜索」
#   PORT=8899 python tools/mock_vod_server.py
#   PORT=8900 python tools/mock_vod_server.py
PORT = int(os.environ.get("PORT", "8899"))
BASE = f"http://127.0.0.1:{PORT}"

TITLES = [
    "雾港谜案", "山海长歌", "星轨之外", "夜行者档案", "晨昏之间",
    "第九个夏天", "玻璃回声", "南方来信", "冬泳计划", "折光之城",
    "无声剧场", "白鲸之约", "十字路口的风", "慢速飞行", "深蓝回声",
    "旧城手记", "第七码头", "雾散之前", "林间信号", "海雾档案",
    "漫长的告白", "空房间", "月光路线", "北纬四十度", "倒数三十天",
    "浮岛记事", "雨后第二街", "光年外卖", "空白档期", "远山与海",
]

CATEGORIES = [
    {"type_id": 1, "type_pid": 0, "type_name": "电影片"},
    {"type_id": 2, "type_pid": 0, "type_name": "连续剧"},
    {"type_id": 3, "type_pid": 0, "type_name": "综艺片"},
    {"type_id": 4, "type_pid": 0, "type_name": "动漫片"},
]

AREAS = ["中国大陆", "中国香港", "日本", "韩国", "欧美"]
LANGS = ["国语", "粤语", "英语", "日语"]
ACTORS = ["林青野", "周慕白", "苏见夏", "程亦然", "沈砚", "顾长风"]
DIRECTORS = ["陈以安", "许望", "叶知秋", "黎川"]

SYNOPSIS = (
    "一段被海雾封存的往事，在十年后被人重新翻开。"
    "当所有人都在讲述同一个夜晚，只有细节彼此矛盾。"
    "本故事纯属虚构，仅用于界面演示。"
)


def build_items():
    rng = random.Random(8899)
    items = []

    for i, title in enumerate(TITLES, start=1):
        cat = CATEGORIES[(i - 1) % len(CATEGORIES)]
        ep_count = 12 if cat["type_id"] in (2, 3, 4) else 1

        eps = []
        for e in range(1, ep_count + 1):
            name = f"第{e:02d}集" if ep_count > 1 else "正片"
            eps.append(f"{name}${BASE}/hls/{i}/{e}.m3u8")

        items.append({
            "vod_id": 1000 + i,
            "type_id": cat["type_id"],
            "type_id_1": cat["type_id"],
            "type_name": cat["type_name"],
            "vod_name": title,
            "vod_sub": "",
            "vod_class": rng.choice(["悬疑", "剧情", "科幻", "家庭", "纪录"]),
            "vod_pic": f"{BASE}/posters/{i:03d}.jpg",
            "vod_actor": "、".join(rng.sample(ACTORS, 3)),
            "vod_director": rng.choice(DIRECTORS),
            "vod_content": SYNOPSIS,
            "vod_blurb": SYNOPSIS,
            "vod_remarks": (f"更新至{ep_count}集" if ep_count > 1 else "HD 国语"),
            "vod_year": str(2024 + (i % 3)),
            "vod_area": rng.choice(AREAS),
            "vod_lang": rng.choice(LANGS),
            "vod_duration": f"{rng.randint(42, 132)}分钟",
            "vod_pubdate": f"2026-{rng.randint(1, 9):02d}-{rng.randint(1, 28):02d}",
            "vod_time": "2026-10-20 12:00:00",
            "vod_score": f"{rng.uniform(6.8, 9.4):.1f}",
            "vod_total": 0,
            "vod_isend": 1 if ep_count > 1 and i % 3 == 0 else 0,
            "vod_play_from": "演示线路$$$备用线路",
            "vod_play_url": "#".join(eps) + "$$$" + "#".join(eps),
        })

    return items


ITEMS = build_items()
PAGE_SIZE = 20


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def send_json(self, payload):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(body)

    def send_text(self, text):
        body = text.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def send_file(self, path, ctype="image/jpeg"):
        try:
            with open(path, "rb") as f:
                body = f.read()
        except OSError:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "max-age=3600")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        parsed = urlparse(self.path)
        path = parsed.path
        qs = parse_qs(parsed.query)

        if path.startswith("/posters/"):
            name = os.path.basename(path)
            self.send_file(os.path.join(POSTERS, name))
            return

        # 订阅链接（自检用）：JSON 数组，其中一条地址重复，应被去重跳过
        if path.rstrip("/") == "/sub.json":
            self.send_json([
                {"name": "示例源 A", "api": f"{BASE}/api.php/provide/vod"},
                {"name": "示例源 B", "api": "https://example.org/api.php/provide/vod"},
                {"name": "示例源 A 副本", "api": f"{BASE}/api.php/provide/vod"},
            ])
            return

        # 订阅链接（自检用）：纯文本，一行一条，带注释与 "名称,地址" 写法
        if path.rstrip("/") == "/sub.txt":
            self.send_text(
                "# 订阅示例：一行一个接口地址\n"
                "# 也可以用「名称,地址」的写法\n"
                f"{BASE}/api.php/provide/vod\n"
                "示例源 C,https://example.com/api.php/provide/vod\n"
            )
            return

        if path.rstrip("/") == "/api.php/provide/vod":
            ac = (qs.get("ac") or ["detail"])[0]

            if ac == "list":
                self.send_json({
                    "code": 1, "msg": "数据列表", "page": 1, "pagecount": 1,
                    "limit": "20", "total": len(ITEMS),
                    "list": [{"vod_id": it["vod_id"], "vod_name": it["vod_name"]} for it in ITEMS],
                    "class": CATEGORIES,
                })
                return

            # 详情
            ids = (qs.get("ids") or [""])[0]
            if ids:
                try:
                    wanted = {int(x) for x in ids.split(",") if x.strip()}
                except ValueError:
                    wanted = set()
                picked = [it for it in ITEMS if it["vod_id"] in wanted]
                self.send_json({
                    "code": 1, "msg": "数据列表", "page": 1,
                    "pagecount": 1, "total": len(picked), "list": picked,
                })
                return

            # 列表 / 搜索
            result = ITEMS
            wd = (qs.get("wd") or [""])[0].strip()
            if wd:
                result = [it for it in result if wd in it["vod_name"]]

            tid = (qs.get("t") or [""])[0]
            if tid.isdigit():
                result = [it for it in result if it["type_id"] == int(tid)]

            page = (qs.get("pg") or ["1"])[0]
            page = int(page) if page.isdigit() else 1
            total = len(result)
            pages = max(1, (total + PAGE_SIZE - 1) // PAGE_SIZE)
            start = (page - 1) * PAGE_SIZE

            self.send_json({
                "code": 1, "msg": "数据列表",
                "page": page, "pagecount": pages,
                "limit": str(PAGE_SIZE), "total": total,
                "list": result[start:start + PAGE_SIZE],
            })
            return

        self.send_error(404)


def main():
    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"mock vod api on {BASE}/api.php/provide/vod")
    server.serve_forever()


if __name__ == "__main__":
    main()
