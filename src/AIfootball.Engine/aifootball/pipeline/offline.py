"""离线 Pipeline — 完整的 3D 重建 + 弹道拟合 + 导出流程"""
import sys
from pathlib import Path
from typing import Optional

from ultralytics import YOLO

from aifootball.config import Config
from aifootball.reconstruction.triangulator import load_camera_configs, process_sample as recon_sample
from aifootball.trajectory.ballistic_fit import process_sample as ballistic_sample
from aifootball.trajectory.export import convert_sample


class OfflinePipeline:
    """离线处理流水线"""

    def __init__(self, cfg: Config):
        self.cfg = cfg
        cfg.ensure_dirs()

    def run(
        self,
        samples: Optional[list[str]] = None,
        yolo_model: str = "yolo11m.pt",
        imgsz: int = 1280,
        conf: float = 0.15,
        skip_reconstruct: bool = False,
        skip_ballistic: bool = False,
    ) -> bool:
        """执行完整离线流水线"""
        # 自动扫描样本
        if samples is None:
            samples = [d.name for d in self.cfg.samples_dir.iterdir()
                       if d.is_dir() and len(list(d.glob("*.mp4"))) >= 2]

        if not samples:
            print("未找到可用样本（需要每个样本目录含 2 个 mp4）")
            return False

        print(f"\n{'='*50}")
        print(f"  离线处理: {len(samples)} 个样本")
        print(f"{'='*50}")

        # 加载相机标定
        camera_configs = load_camera_configs(self.cfg)

        # 加载 YOLO
        model = None
        if not skip_reconstruct:
            model_path = self.cfg.yolo_model_path
            if not Path(model_path).exists():
                print(f"YOLO 模型不存在，将自动下载: {model_path}")
            model = YOLO(str(model_path))

        all_ok = True
        completed = []

        for name in samples:
            print(f"\n--- [{name}] ---")
            sample_dir = self.cfg.samples_dir / name
            videos = sorted(sample_dir.glob("*.mp4"))

            if len(videos) != 2:
                print(f"[{name}] 需要正好 2 个 mp4，找到 {len(videos)} 个")
                all_ok = False
                continue

            # Step 1: 3D 重建
            if not skip_reconstruct:
                print(f"[{name}] 步骤 1/3: 3D 重建...")
                try:
                    n_pts = recon_sample(sample_dir, self.cfg, camera_configs,
                                         model=model, imgsz=imgsz, conf=conf)
                    print(f"[{name}] 3D 重建完成 ({n_pts} 个空间点)")
                except Exception as e:
                    print(f"[{name}] 3D 重建失败: {e}")
                    all_ok = False
                    continue

            # Step 2: 弹道拟合
            if not skip_ballistic:
                print(f"[{name}] 步骤 2/3: 弹道拟合...")
                try:
                    ballistic_sample(name, self.cfg)
                except Exception as e:
                    print(f"[{name}] 弹道拟合失败: {e}")
                    all_ok = False
                    continue

            # Step 3: 导出 Unity CSV
            print(f"[{name}] 步骤 3/3: 导出 Unity CSV...")
            try:
                convert_sample(name, self.cfg)
                completed.append(name)
            except Exception as e:
                print(f"[{name}] 导出失败: {e}")
                all_ok = False

        print(f"\n{'='*50}")
        status = "全部完成" if all_ok else "部分失败"
        print(f"  {status} | 成功: {len(completed)}/{len(samples)}")
        print(f"{'='*50}")

        return all_ok
