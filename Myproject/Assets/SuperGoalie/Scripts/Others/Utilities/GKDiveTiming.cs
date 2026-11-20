using System.Collections.Generic;

namespace Assets.SuperGoalie.Scripts.Others.Utilities
{
    /// <summary>
    /// 门将扑救动作的时间/位移标定表。
    ///
    /// 数据来源：Blender 逐帧采样 output/blender/goalkeeper_mixamo.blend（30fps）——
    /// 对手腕相对髋部的伸展度做曲线，找出「手伸到最远」的帧，作为与球接触的时刻；
    /// 同时记录髋部相对根骨骼的最大横向位移，作为动作自带的扑救覆盖距离。
    ///
    /// 关键结论：这些 Mixamo 动作全长 2.5–3.5 秒，其中接触点只落在 18%–30% 处，
    /// 后面 70% 是倒地与起身。因此必须「提前起跳 + 接触帧对齐」，不能等球到了才播，
    /// 也不能在球到点时把动作掐断。
    /// </summary>
    public static class GKDiveTiming
    {
        public struct Dive
        {
            /// <summary>动作时长（秒，30fps 帧数 / 30）。</summary>
            public float Length;
            /// <summary>接触帧在整段动作里的归一化位置（0–1）。</summary>
            public float ContactNorm;
            /// <summary>动作自带的髋部横向位移（米，绝对值）。</summary>
            public float LateralTravel;
            /// <summary>倒地完成（可以开始起身）的归一化位置。</summary>
            public float RecoverNorm;
        }

        static readonly Dictionary<string, Dive> Table = new Dictionary<string, Dive>
        {
            // state           时长      接触点   自带横移   倒地完成
            { "Punch_Low_Left",    new Dive { Length = 3.4333f, ContactNorm = 0.216f, LateralTravel = 1.677f, RecoverNorm = 0.62f } },
            { "Punch_Low_Right",   new Dive { Length = 3.2667f, ContactNorm = 0.216f, LateralTravel = 2.010f, RecoverNorm = 0.62f } },
            { "Punch_Low_Center",  new Dive { Length = 2.7333f, ContactNorm = 0.469f, LateralTravel = 0.437f, RecoverNorm = 0.78f } },
            { "Punch_Mid_Left",    new Dive { Length = 3.4667f, ContactNorm = 0.291f, LateralTravel = 0.875f, RecoverNorm = 0.60f } },
            { "Punch_Mid_Right",   new Dive { Length = 2.5000f, ContactNorm = 0.257f, LateralTravel = 0.759f, RecoverNorm = 0.60f } },
            { "Punch_Mid_Center",  new Dive { Length = 1.3000f, ContactNorm = 0.184f, LateralTravel = 0.238f, RecoverNorm = 0.70f } },
            { "Punch_High_Left",   new Dive { Length = 2.7667f, ContactNorm = 0.183f, LateralTravel = 0.374f, RecoverNorm = 0.66f } },
            { "Punch_High_Center", new Dive { Length = 2.7667f, ContactNorm = 0.183f, LateralTravel = 0.374f, RecoverNorm = 0.66f } },
            { "Punch_High_Right",  new Dive { Length = 2.7667f, ContactNorm = 0.183f, LateralTravel = 0.374f, RecoverNorm = 0.66f } },
        };

        static readonly Dive Fallback = new Dive { Length = 2.5f, ContactNorm = 0.25f, LateralTravel = 0.8f, RecoverNorm = 0.65f };

        public static Dive Get(string state)
        {
            Dive d;
            return Table.TryGetValue(state, out d) ? d : Fallback;
        }

        /// <summary>
        /// 按「高度档 + 左右 + 需要横移的距离」挑一条动作。
        ///
        /// 之所以带距离：9 个格子里动作自带的覆盖距离差很多（0.24 m 到 2.01 m），
        /// 用 2 米的侧扑去接一个 0.4 米的近角球，人一定扑过头，看起来就假。
        /// 这里挑「自带位移最接近实际需要」的那一格。
        /// </summary>
        public static string SelectState(float turn, float height, float lateralNeed)
        {
            string side = turn < -0.01f ? "Left" : (turn > 0.01f ? "Right" : "Center");
            string band = height < 0.25f ? "Low" : (height < 0.60f ? "Mid" : "High");

            if (side == "Center")
            {
                // 中路球不做侧扑：低球弯腰捡，中球胸前接，高球原地起跳接。
                return "Punch_" + band + "_Center";
            }

            // 侧向：在本档以及相邻档的侧向候选里，挑位移最贴近需要值的一条。
            var candidates = new List<string>();
            candidates.Add("Punch_" + band + "_" + side);

            if (band == "Low")
            {
                // 低球既可以贴地扑（DivingSave，覆盖 1.7–2.0 m），也可以用身体挡（0.76–0.88 m）。
                candidates.Add("Punch_Mid_" + side);
            }
            else if (band == "Mid")
            {
                candidates.Add("Punch_Low_" + side);
            }

            string best = candidates[0];
            float bestError = float.MaxValue;
            foreach (var c in candidates)
            {
                float err = UnityEngine.Mathf.Abs(Get(c).LateralTravel - lateralNeed);
                if (err < bestError)
                {
                    bestError = err;
                    best = c;
                }
            }
            return best;
        }
    }
}
