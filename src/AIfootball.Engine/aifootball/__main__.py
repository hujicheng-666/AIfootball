"""AIfootball Engine 入口：`python -m aifootball ...`，也可直接执行本文件。

界面（WPF）是以 `python -u <EngineDir>/aifootball/__main__.py <子命令>` 的
方式调用的，此时 sys.path[0] 是包内目录而不是包根，`import aifootball`
会失败（嵌入式 Python 处于 _pth 隔离模式时连 PYTHONPATH 都不生效）。
所以这里显式把包根插到 sys.path 最前面，两种调用方式都能跑通。
"""
import os
import sys

if __package__ in (None, ""):
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    __package__ = "aifootball"

from aifootball import cli  # noqa: E402  （必须在 sys.path 修正之后导入）

if __name__ == "__main__":
    sys.exit(cli.main())
