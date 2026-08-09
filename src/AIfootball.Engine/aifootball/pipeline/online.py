"""在线 Pipeline — 录制 + 处理一体化"""
from aifootball.config import Config
from aifootball.capture.dual_camera import DualCameraRecorder
from aifootball.pipeline.offline import OfflinePipeline


class OnlinePipeline:
    """在线处理流水线: 录制双摄像头视频，然后执行完整分析"""

    def __init__(self, cfg: Config):
        self.cfg = cfg
        cfg.ensure_dirs()

    def run(
        self,
        cam_left="0",
        cam_right="1",
        sample_name: str = "sample_live",
        yolo_model: str = "yolo11m.pt",
    ) -> bool:
        """录制并处理"""
        sample_dir = self.cfg.samples_dir / sample_name

        # Step 1: 录制
        print(f"\n在线模式 -> {sample_dir}")
        recorder = DualCameraRecorder(cam_left, cam_right, sample_dir)

        if not recorder.start():
            print("用户取消录制")
            return False

        # Step 2: 离线处理
        offline = OfflinePipeline(self.cfg)
        return offline.run(
            samples=[sample_name],
            yolo_model=yolo_model,
        )
