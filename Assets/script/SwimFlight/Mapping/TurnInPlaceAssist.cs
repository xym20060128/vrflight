using UnityEngine;
using SwimFlight.Core;

namespace SwimFlight.Mapping
{
    /// <summary>
    /// 原地转弯辅助 (Turn-In-Place Assist)
    ///
    /// 问题:
    ///   本模型里"原地转弯"的物理形式本来就存在 —— 两手绕身体竖直轴做**同向**切向划水时,
    ///     力矩  τ_y = Σ (r_z·F_x − r_x·F_z) ≠ 0   (产生偏航)
    ///   但孪生的净力 FX = Σ F_x 一般不为零, 于是"转弯"总是附带一段侧向平移。
    ///
    ///   为什么净力不为零: 双手绕竖直轴扫动时 v_i = ω × r_i, 反作用力 F_i = −k·v_i,
    ///     净力 = −k·ω × (r_L + r_R)。
    ///     只有 r_L = −r_R (两手正好前后相对) 时它才严格为零;
    ///     真实姿态下两手都在身前 (r_L ≈ (−a, y, c)、r_R ≈ (+a, y, c)), r_L+r_R ≈ 2c·ẑ ≠ 0,
    ///     净力就是一个**纯侧向**的残差, 转得越用力越大。
    ///
    /// ---- 旧判据为什么没修好 (2026-10-07 实测) ----
    ///   旧版用「对抗度」opposition = 1 − |F_L+F_R| / (|F_L|+|F_R|) 判意图, 对**力向量反平行**
    ///   的手势有效 (如一手前推、一手后拉)。但真实转弯手势是"绕竖直轴同向扫动", 两手
    ///   力向量彼此夹角约 90° (v_L = ω×r_L, v_R = ω×r_R, r_L 与 r_R 不平行时两者不平行),
    ///   实测 opposition = **0.293** < 入场阈值 0.5 → **辅助从不介入**, 净力原样进入动力学。
    ///
    /// ---- 本版做法 ----
    ///   1) 意图判据换成**绕竖直轴的力矩同向度** (量纲无关):
    ///        τ_i = (r_i,xz × F_i,xz)·ŷ            每只手对偏航的贡献
    ///        coherence = |τ_L + τ_R| / (|τ_L| + |τ_R|) ∈ [0,1]
    ///          = 1  两手把身体往同一个方向拧  → 转向手势
    ///          = 0  两手互相抵消 (双手前划 / 双手刹车) → 纯平移, 不介入
    ///      并对「两只手都要出力」加一道力占比门限, 避免单手划水被误判成转向。
    ///
    ///   2) 抵消净力的方式换成**径向修正** —— 这是本版的关键:
    ///        把每只手各自沿 r̂_i,xz 方向加一份修正力 ΔF_i = δ_i·r̂_i,xz。
    ///        因为 r_i,xz × r̂_i,xz ≡ 0, 径向力对**绕竖直轴的力矩贡献恒为 0**,
    ///        所以「净水平力被抵消」与「偏航力矩分毫不动」可以同时成立。
    ///        解 2×2 线性方程组 [r̂_L r̂_R]·[δ_L; δ_R] = −scale·F_net,xz 即可。
    ///
    ///      求解结果恰好把"双手同向横扫"还原成"一手前一手后"的纯转向力型, 例如
    ///        F_L = F_R = (1,0,0), r = (±0.35, ·, 0.35)
    ///          → 修正后 F_L = (0,0,1)、F_R = (0,0,−1)：净力 0, τ_y 不变。
    ///
    ///      力臂方向接近平行时 (行列式→0) 直接求逆会放大成巨大修正量, 所以用 Tikhonov
    ///      正则化 (λ = leverageDamping²): 退化成"只抵消落在两个力臂方向张成平面内的
    ///      那部分净力"。几何上确实抵消不掉的切向净力留着, 但绝不产生副作用;
    ///      再加一道修正量幅值上限兜底。任何 δ 都不影响 τ_y, 所以"少抵消"永远安全。
    ///
    ///   3) 只抵消**水平**净力。竖直通道保持原样: 转向手势里的共同升降不算"转弯带的平移",
    ///      不能顺手抹掉 (三维移动能力要保持)。
    ///
    /// 分工: 本类只改输入力, 不碰动力学、不碰速度状态、不碰 XR Origin。
    ///   "转弯时不漂"由本类负责, "转弯时头不被甩出去"由 XRLocomotionApplierV2 的
    ///   绕头部支点旋转负责 (见 [[swimflight-yaw-pivot]])。
    /// </summary>
    [System.Serializable]
    public class TurnInPlaceAssist
    {
        [Tooltip("原地转弯辅助总开关。关闭时左右手力原样透传 (改动前行为)。")]
        public bool enabled = true;

        [Tooltip("开始介入的「力矩同向度」[0,1]。0.5 = 两手对偏航的贡献同向度超过一半才介入。")]
        [Range(0f, 1f)] public float coherenceEnter = 0.5f;

        [Tooltip("完全介入的「力矩同向度」[0,1]。到这一步净水平力被完全抵消, 只剩旋转。")]
        [Range(0f, 1f)] public float coherenceFull = 0.85f;

        [Tooltip("最多抵消多少净水平力 [0,1]。1 = 原地纯转, 0.5 = 只抵消一半。")]
        [Range(0f, 1f)] public float strength = 1f;

        [Tooltip("单只手的力低于此值 (N) 视为该手没有参与。这是**渐入起点**不是开关。\n" +
                 "单手划水主要由 minTorqueShare 挡住, 这里只是兜底, 故取低值避免误伤轻力转弯。")]
        public float minHandForce = 0.25f;

        [Tooltip("单只手的力达到此值 (N) 时该手被完全计入。与 minHandForce 之间是平滑渐入。")]
        public float fullHandForce = 0.6f;

        [Tooltip("较弱那只手的偏航力矩份额渐入起点 [0,1]。\n" +
                 "0.25 = 两只手都要对转向有实质贡献; 单手划水 (另一手≈0) 份额≈0, 不受影响。")]
        [Range(0f, 1f)] public float minTorqueShare = 0.25f;

        [Tooltip("较弱那只手的偏航力矩份额达到此值时完全介入 [0,1]。与 minTorqueShare 之间是平滑渐入。")]
        [Range(0f, 1f)] public float fullTorqueShare = 0.4f;

        [Tooltip("径向修正的正则化系数 (Tikhonov λ)。越小抵消越彻底; 越大力臂方向接近平行时越保守。\n" +
                 "0.05 → 良态姿态下净水平力残留约 0.3%, 力臂接近平行时自动少抵消而不是放大修正量。")]
        [Range(0.001f, 0.5f)] public float leverageDamping = 0.05f;

        [Tooltip("修正量上限 (相对 |F_L|+|F_R|)。两手力臂方向接近平行时方程接近奇异, " +
                 "正则化后仍可能算出偏大的修正量; 超过此倍数就按比例缩小 (只会少抵消, 不会反向)。")]
        [Range(0.5f, 5f)] public float maxCorrectionRatio = 2f;

        /// <summary>本帧的力矩同向度 [0,1] (诊断)。1 = 两手同向拧身体 (转向手势)。</summary>
        public float LastCoherence { get; private set; }

        /// <summary>本帧较弱手的偏航力矩占比 [0,1] (诊断)。</summary>
        public float LastTorqueShare { get; private set; }

        /// <summary>本帧的旧版对抗度 [0,1] (诊断对照, 不再作为判据)。</summary>
        public float LastOpposition { get; private set; }

        /// <summary>本帧施加在净水平力上的抵消比例 [0,1] (诊断)。1 = 完全不介入。</summary>
        public float LastTranslationScale { get; private set; } = 1f;

        /// <summary>本帧是否已介入 (诊断)。</summary>
        public bool Active { get; private set; }

        /// <summary>本帧是否走了径向修正 (false 且 Active=true 表示退回共用/差模缩放)。</summary>
        public bool UsedRadialCorrection { get; private set; }

        public void Reset()
        {
            LastCoherence = 0f;
            LastTorqueShare = 0f;
            LastOpposition = 0f;
            LastTranslationScale = 1f;
            Active = false;
            UsedRadialCorrection = false;
        }

        /// <summary>
        /// 就地改写 command 的左右手力。必须在动力学积分之前调用。
        /// </summary>
        public void Apply(ref FlightCommand command)
        {
            if (!enabled)
            {
                Reset();
                return;
            }

            Vector3 left = command.leftForce;
            Vector3 right = command.rightForce;
            float leftMagnitude = left.magnitude;
            float rightMagnitude = right.magnitude;

            // 双手都没力 (停手/回收): 不介入。这是唯一保留的硬门限 ——
            // 此时所有量都趋近于 0, 开关也不会带来可见跳变。
            if (leftMagnitude + rightMagnitude < 1e-5f)
            {
                Reset();
                return;
            }

            // 每只手对偏航的贡献: τ_i,y = (r_i,xz × F_i,xz)·ŷ = r_z·F_x − r_x·F_z
            // 竖直力臂分量 r_y 对绕竖直轴的力矩没有贡献, 所以只用水平分量。
            Vector3 leftArm = command.leftArm;
            Vector3 rightArm = command.rightArm;
            float torqueLeft = leftArm.z * left.x - leftArm.x * left.z;
            float torqueRight = rightArm.z * right.x - rightArm.x * right.z;
            float torqueSum = Mathf.Abs(torqueLeft) + Mathf.Abs(torqueRight);

            LastOpposition = ComputeOpposition(left, right, leftMagnitude, rightMagnitude);

            if (torqueSum < 1e-6f)
            {
                // 两手都不产生偏航 (纯平移手势): 不介入
                LastCoherence = 0f;
                LastTorqueShare = 0f;
                LastTranslationScale = 1f;
                Active = false;
                UsedRadialCorrection = false;
                return;
            }

            float coherence = Mathf.Clamp01(Mathf.Abs(torqueLeft + torqueRight) / torqueSum);
            float torqueShare = Mathf.Clamp01(Mathf.Min(Mathf.Abs(torqueLeft), Mathf.Abs(torqueRight))
                                              / Mathf.Max(Mathf.Abs(torqueLeft), Mathf.Abs(torqueRight)));
            LastCoherence = coherence;
            LastTorqueShare = torqueShare;

            // 三道判据全部走**连续渐入**, 不做开关:
            //   ① coherence      —— 两手同向拧身体的程度
            //   ② 弱手力幅        —— 单手划水/回收的手不计入
            //   ③ 弱手力矩份额    —— 单手或不对称推进不判成转向
            // 任一判据做硬开关时, 它在阈值附近抖动会让施加的力整帧跳变 (修正量可达输入力的
            // 2 倍), 体感就是转弯时画面抖 —— 这正是本次要消掉的东西。
            float minHandMagnitude = Mathf.Min(leftMagnitude, rightMagnitude);
            float handFactor = Ramp(minHandForce, fullHandForce, minHandMagnitude);
            float shareFactor = Ramp(minTorqueShare, fullTorqueShare, torqueShare);
            float coherenceFactor = Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(coherenceEnter, coherenceFull, coherence));

            float engagement = coherenceFactor * handFactor * shareFactor;
            float scale = 1f - Mathf.Clamp01(strength) * engagement;

            LastTranslationScale = scale;
            Active = scale < 1f - 1e-5f;
            UsedRadialCorrection = false;

            if (!Active) return;

            // 净水平力的抵消目标: 要加上的修正量 = −(净水平力)。scale=1 (不介入) 时为 0,
            // scale=0 (完全介入) 时正好把净水平力抵到零。
            float cancel = 1f - scale;
            float targetX = -(left.x + right.x) * cancel;
            float targetZ = -(left.z + right.z) * cancel;

            ApplyRadialCorrection(ref command, targetX, targetZ);
            UsedRadialCorrection = true;
        }

        /// <summary>
        /// 径向修正: 对每只手沿 r̂_i,xz 加一份力, 使水平净力抵消而偏航力矩不变。
        ///
        /// 因为 r_i,xz 与 r̂_i,xz 平行 ⇒ r_i,xz × (δ_i·r̂_i,xz) = 0 ⇒ **任何 δ_i 都不产生
        /// 绕竖直轴的力矩**。所以只需解 2×2 方程 [r̂_L r̂_R]·[δ_L; δ_R] = target,
        /// 力矩天然分毫不动。
        ///
        /// 用 Tikhonov 正则化 (λ = leverageDamping²) 而不是直接求逆:
        ///   两手力臂方向接近平行时矩阵接近奇异, 直接求逆会放大成巨大修正量。
        ///   正则化版本退化为"只抵消落在两个力臂方向张成平面内的那部分净力" ——
        ///   几何上确实做不到的部分 (力臂共线时的切向净力) 留着, 绝不产生副作用。
        /// </summary>
        private void ApplyRadialCorrection(ref FlightCommand command, float targetX, float targetZ)
        {
            float leftLength = Mathf.Sqrt(command.leftArm.x * command.leftArm.x
                                        + command.leftArm.z * command.leftArm.z);
            float rightLength = Mathf.Sqrt(command.rightArm.x * command.rightArm.x
                                         + command.rightArm.z * command.rightArm.z);
            if (leftLength < 1e-4f || rightLength < 1e-4f) return;
            if (targetX * targetX + targetZ * targetZ < 1e-12f) return;

            float ulx = command.leftArm.x / leftLength, ulz = command.leftArm.z / leftLength;
            float urx = command.rightArm.x / rightLength, urz = command.rightArm.z / rightLength;
            float cosine = Mathf.Clamp(ulx * urx + ulz * urz, -1f, 1f);

            // A = [1+λ  c ; c  1+λ] —— MᵀM = [1 c; c 1] 加正则化
            float lambda = leverageDamping * leverageDamping;
            float diagonal = 1f + lambda;
            float determinant = diagonal * diagonal - cosine * cosine;
            if (Mathf.Abs(determinant) < 1e-9f) return;

            // Mᵀ·target
            float leftProjection = ulx * targetX + ulz * targetZ;
            float rightProjection = urx * targetX + urz * targetZ;

            float deltaLeft = (diagonal * leftProjection - cosine * rightProjection) / determinant;
            float deltaRight = (diagonal * rightProjection - cosine * leftProjection) / determinant;

            // 幅值保险: 力臂近平行时方程接近奇异, 正则化后仍可能解出偏大的修正量。
            // 修正量只能是"少抵消", 所以按比例缩小永远安全 (τ_y 不受任何 δ 影响)。
            float correctionSum = Mathf.Abs(deltaLeft) + Mathf.Abs(deltaRight);
            float inputSum = command.leftForce.magnitude + command.rightForce.magnitude;
            float limit = Mathf.Max(1e-6f, inputSum) * Mathf.Max(0.5f, maxCorrectionRatio);
            if (correctionSum > limit && correctionSum > 1e-6f)
            {
                float shrink = limit / correctionSum;
                deltaLeft *= shrink;
                deltaRight *= shrink;
            }

            command.leftForce = new Vector3(command.leftForce.x + deltaLeft * ulx,
                                            command.leftForce.y,
                                            command.leftForce.z + deltaLeft * ulz);
            command.rightForce = new Vector3(command.rightForce.x + deltaRight * urx,
                                             command.rightForce.y,
                                             command.rightForce.z + deltaRight * urz);
        }

        /// <summary>
        /// 平滑渐入 [from, to] → [0, 1]。from ≥ to 时退化为阶跃 (仅用于防御性用法)。
        /// 用 SmoothStep 而不是线性, 使介入量的**一阶导**在两端也为 0, 阈值附近不会留下折角。
        /// </summary>
        private static float Ramp(float from, float to, float value)
        {
            if (to <= from) return value >= to ? 1f : 0f;
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(from, to, value));
        }

        private static float ComputeOpposition(Vector3 left, Vector3 right,
            float leftMagnitude, float rightMagnitude)
        {
            float sum = leftMagnitude + rightMagnitude;
            if (sum < 1e-6f) return 0f;
            return Mathf.Clamp01(1f - (left + right).magnitude / sum);
        }
    }
}
