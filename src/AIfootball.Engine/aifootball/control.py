"""采集进程的控制通道：界面通过 stdin 下发 start / stop。

为什么用 stdin 行协议：采集进程本来就是界面启动的子进程，stdin 管道现成，
不需要新开端口、也不需要在磁盘上轮询指令文件。读取放在后台线程，采集循环
只做非阻塞的 poll()，不会拖慢取流。

命令行直接运行时 stdin 是终端，这里不占用它，改由预览窗口的 ENTER / Q 控制，
两种用法互不干扰（`interactive` 也用来决定要不要弹本机预览窗口）。
"""
import queue
import sys
import threading

START = "start"
STOP = "stop"


class ControlChannel:
    """收集界面下发的指令；终端交互运行时保持空闲。"""

    def __init__(self):
        self._pending = queue.Queue()
        self.interactive = True     # True = 终端交互运行，由键盘控制
        # 上级（界面）退出后 stdin 会关闭。采集进程必须据此自行结束：
        # 否则它会变成孤儿，继续占着 UDP 端口抢收数据包，下一次预览
        # 既绑不上端口、收到的码流也是残缺的。
        self.closed = False
        try:
            stream = sys.stdin
        except Exception:
            stream = None
        if stream is None or getattr(stream, "closed", True):
            return
        try:
            self.interactive = bool(stream.isatty())
        except Exception:
            self.interactive = False
        if self.interactive:
            return
        try:
            threading.Thread(target=self._read_lines, args=(stream,), daemon=True,
                             name="control-stdin").start()
        except Exception:
            pass

    def _read_lines(self, stream):
        try:
            for line in stream:
                command = line.strip().lower()
                if command:
                    self._pending.put(command)
        except Exception:
            pass
        finally:
            self.closed = True

    def poll(self):
        """取出全部待处理指令（不阻塞）。"""
        commands = []
        while True:
            try:
                commands.append(self._pending.get_nowait())
            except queue.Empty:
                return commands
