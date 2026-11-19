"""ControlChannel 指令轮询测试（用假 stdin，不依赖界面进程）。"""
import io
import sys
import time
import unittest

from aifootball.control import START, STOP, ControlChannel


class _FakePipe(io.StringIO):
    """模拟界面通过管道下发的 stdin：不是 TTY。"""

    def isatty(self):
        return False


class _FakeTerminal(io.StringIO):
    """模拟终端交互运行的 stdin：是 TTY。"""

    def isatty(self):
        return True


def _poll_until(channel, expected, timeout=2.0):
    """后台线程异步读 stdin，这里轮询等待指令到齐。"""
    deadline = time.time() + timeout
    commands = []
    while time.time() < deadline:
        commands = channel.poll()
        if len(commands) >= expected:
            return commands
        time.sleep(0.01)
    return commands


class ControlChannelTest(unittest.TestCase):

    def _make_channel(self, stream):
        original = sys.stdin
        sys.stdin = stream
        try:
            return ControlChannel()
        finally:
            sys.stdin = original

    def test_pipe_commands_are_collected_and_empties_ignored(self):
        channel = self._make_channel(_FakePipe("start\n\n  stop  \n"))
        self.assertFalse(channel.interactive)
        self.assertEqual(_poll_until(channel, 2), [START, STOP])
        self.assertEqual(channel.poll(), [])

    def test_commands_are_lowercased_and_stripped(self):
        channel = self._make_channel(_FakePipe("  START \r\nSTOP\r\n"))
        self.assertEqual(_poll_until(channel, 2), ["start", "stop"])

    def test_closed_flag_is_set_at_eof(self):
        """上级进程退出后 stdin 关闭，采集循环要能据此自行收尾。"""
        channel = self._make_channel(_FakePipe(""))
        deadline = time.time() + 2.0
        while not channel.closed and time.time() < deadline:
            time.sleep(0.01)
        self.assertTrue(channel.closed)
        self.assertEqual(channel.poll(), [])

    def test_interactive_terminal_does_not_consume_stdin(self):
        """终端交互运行时由键盘控制，控制通道保持空闲、不抢 stdin。"""
        channel = self._make_channel(_FakeTerminal("start\n"))
        self.assertTrue(channel.interactive)
        self.assertFalse(channel.closed)
        self.assertEqual(channel.poll(), [])

    def test_poll_is_non_blocking_when_queue_empty(self):
        channel = self._make_channel(_FakePipe("start\n"))
        self.assertEqual(_poll_until(channel, 1), [START])
        started = time.monotonic()
        self.assertEqual(channel.poll(), [])
        self.assertLess(time.monotonic() - started, 0.5)


if __name__ == "__main__":
    unittest.main()
