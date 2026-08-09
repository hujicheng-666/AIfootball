import json
import os

output_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "runtime", "data", "goalkeepers")
os.makedirs(output_dir, exist_ok=True)

goalkeepers = [
    {
        "id": "GK_Elite",
        "DisplayName": "世界级门将",
        "Description": "全方位无死角，扑救成功率极高。",
        "Height": 1.95, "DiveSpeed": 5.0, "JumpDistance": 1.2, "JumpHeight": 0.7,
        "Reach": 0.6, "TendGoalSpeed": 3.5, "TendGoalDistance": 2.8, "GoalKeeping": 0.93,
        "SidePreference": 0.0, "HeightPreference": 0.0,
        "ProbabilityMap": {
            "LowLeft": 0.94, "LowMidLeft": 0.95, "LowCenter": 0.97, "LowMidRight": 0.95, "LowRight": 0.94,
            "MidLeft": 0.90, "MidMidLeft": 0.92, "MidCenter": 0.95, "MidMidRight": 0.92, "MidRight": 0.90,
            "HighLeft": 0.78, "HighMidLeft": 0.82, "HighCenter": 0.88, "HighMidRight": 0.82, "HighRight": 0.78,
        }
    },
    {
        "id": "GK_AllRounder",
        "DisplayName": "均衡型门将",
        "Description": "各项能力均衡，没有明显短板。",
        "Height": 1.88, "DiveSpeed": 4.0, "JumpDistance": 1.0, "JumpHeight": 0.5,
        "Reach": 0.5, "TendGoalSpeed": 3.0, "TendGoalDistance": 3.0, "GoalKeeping": 0.85,
        "SidePreference": 0.0, "HeightPreference": 0.0,
        "ProbabilityMap": {
            "LowLeft": 0.85, "LowMidLeft": 0.88, "LowCenter": 0.92, "LowMidRight": 0.88, "LowRight": 0.85,
            "MidLeft": 0.80, "MidMidLeft": 0.83, "MidCenter": 0.88, "MidMidRight": 0.83, "MidRight": 0.80,
            "HighLeft": 0.60, "HighMidLeft": 0.65, "HighCenter": 0.72, "HighMidRight": 0.65, "HighRight": 0.60,
        }
    },
    {
        "id": "GK_WeakLowRight",
        "DisplayName": "右下盲区型",
        "Description": "整体不错，但右下角是致命弱点。",
        "Height": 1.90, "DiveSpeed": 4.2, "JumpDistance": 1.05, "JumpHeight": 0.55,
        "Reach": 0.5, "TendGoalSpeed": 3.2, "TendGoalDistance": 3.0, "GoalKeeping": 0.82,
        "SidePreference": -0.3, "HeightPreference": -0.2,
        "ProbabilityMap": {
            "LowLeft": 0.82, "LowMidLeft": 0.78, "LowCenter": 0.85, "LowMidRight": 0.50, "LowRight": 0.30,
            "MidLeft": 0.75, "MidMidLeft": 0.80, "MidCenter": 0.83, "MidMidRight": 0.70, "MidRight": 0.55,
            "HighLeft": 0.55, "HighMidLeft": 0.60, "HighCenter": 0.65, "HighMidRight": 0.50, "HighRight": 0.40,
        }
    },
    {
        "id": "GK_Reactive",
        "DisplayName": "反应型门将",
        "Description": "反应极快，低球无敌，但高空球是弱点。",
        "Height": 1.82, "DiveSpeed": 5.5, "JumpDistance": 1.1, "JumpHeight": 0.4,
        "Reach": 0.45, "TendGoalSpeed": 3.8, "TendGoalDistance": 2.5, "GoalKeeping": 0.88,
        "SidePreference": 0.0, "HeightPreference": -0.5,
        "ProbabilityMap": {
            "LowLeft": 0.90, "LowMidLeft": 0.93, "LowCenter": 0.95, "LowMidRight": 0.93, "LowRight": 0.90,
            "MidLeft": 0.85, "MidMidLeft": 0.88, "MidCenter": 0.92, "MidMidRight": 0.88, "MidRight": 0.85,
            "HighLeft": 0.50, "HighMidLeft": 0.55, "HighCenter": 0.60, "HighMidRight": 0.55, "HighRight": 0.50,
        }
    },
]

for gk in goalkeepers:
    filepath = os.path.join(output_dir, f"{gk['id']}.json")
    with open(filepath, "w", encoding="utf-8") as f:
        json.dump(gk, f, indent=2, ensure_ascii=False)
    print(f"  {gk['id']}.json  — {gk['DisplayName']}")

print(f"\n共 {len(goalkeepers)} 个门将 JSON 已生成到 {output_dir}")
