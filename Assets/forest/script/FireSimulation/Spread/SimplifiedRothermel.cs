using UnityEngine;
using System.Collections.Generic;

namespace FireSimulation
{
    // ═══════════════════════════════════════════════════════════════
    // SpreadResult — 单次传播 Tick 的计算输出
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 改进 Simplified Rothermel 单次 Tick 计算输出。
    /// 火锋半径模型不再需要 ignitionProbability，改为输出火线强度和火锋前进距离。
    /// </summary>
    public struct SpreadResult
    {
        // 传播速率与火锋
        public float spreadRate;           // R (m/s) 此方向的传播速率
        public float firelineIntensity;    // I_B (kW/m) Byram 火线强度
        public float frontAdvance;         // 本 Tick 火锋在此方向前进的距离 (m)

        // 火焰高度与直接点燃
        public float flameHeight;          // 源树当前火焰高度 (m)，从树底算起
        public bool  canDirectIgnite;      // 火焰高度是否超过目标树冠底部 → 可直接点燃

        // 因子分解 (R = R0 × Π factors)
        public float baseSpreadRate;
        public float windFactor;
        public float slopeFactor;
        public float fuelFactor;
        public float moistureFactor;
        public float distanceFactor;
        public float verticalFactor;
        public float flameHeightFactor;    // φ_flame: 火焰高度因子

        // 预干燥
        public float preHeatMoistureDelta;       // 辐射预干燥导致的含水率下降量
        public float convectiveMoistureDelta;    // 对流柱干燥 (仅当目标高于源时)

        // 调试
        public float effectiveWind;
        public float slopeAngleDeg;
        public float sourceToTargetDist;

        public override string ToString()
        {
            return $"R={spreadRate:F3}m/s  I={firelineIntensity:F0}kW/m  " +
                   $"adv={frontAdvance:F2}m  flame={flameHeight:F1}m  " +
                   $"wnd={windFactor:F2}({effectiveWind:F1}m/s)  " +
                   $"slp={slopeFactor:F2}({slopeAngleDeg:F0}°)  " +
                   $"fuel={fuelFactor:F2}  moist={moistureFactor:F2}  dist={distanceFactor:F2}  " +
                   $"vert={verticalFactor:F2}  flameH={flameHeightFactor:F2}  " +
                   $"dry={preHeatMoistureDelta:F3}  convDry={convectiveMoistureDelta:F3}";
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SimplifiedRothermel (改进版)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 改进版 Simplified Rothermel 地表火蔓延模型。
    ///
    /// 核心方程：
    ///   R = R₀ × φ_wind × φ_slope × φ_fuel × φ_moist × φ_dist × φ_vert × φ_flame
    ///
    ///   基础速率 R₀:   查燃料表 (Grass=0.80, Litter=1.00, Hardwood=0.15 m/s)
    ///   风因子 φ_wind:  exp(0.5 × windSpeed × cosθ)，顺风加速逆风抑制
    ///   坡度因子 φ_slope: exp(2.5 × tan(β))，上坡加速(30°≈4.2x)，下坡抑制(-20°≈0.4x)
    ///   燃料因子 φ_fuel: 目标可燃物易感性
    ///   含水率 φ_moist:  1 − moisture/0.60，越湿越难燃
    ///   距离衰减 φ_dist: exp(−0.05 × d)
    ///   垂直因子 φ_vert: exp(3.0 × Δh/d)，烟囱效应加速向上，抑制向下
    ///   火焰因子 φ_flame: √burnHeight，火焰越高辐射越强
    ///
    /// 输出（SpreadResult）：
    ///   spreadRate (m/s)     传播速率
    ///   firelineIntensity     Byram I_B = H × w × R (kW/m)
    ///   frontAdvance         本 Tick 前进距离
    ///   flameHeight          火焰高度 (m) = treeH × burnH × (0.5 + 0.008×I)
    ///   canDirectIgnite      火焰高度超过目标树冠底部 → 可跳过火锋推进直接点燃
    ///   preHeatMoistureDelta 辐射预干燥 ΔM = η×I/(d²+1)×dt
    ///   convectiveMoistureDelta 对流柱干燥 (热气柱上升，仅上方树木)
    ///
    /// 理论基础：
    ///   Rothermel (1972) 地表火方程
    ///   Byram (1959) 火线强度 + 火焰高度
    ///   Van Wagner (1977) 冠层火引发条件
    ///   NIST 单树燃烧实验 (Douglas Fir 3-7m)
    /// </summary>
    public static class SimplifiedRothermel
    {
        // ═══════════════════════════════════════════
        // 扩展燃料参数表 (增加 heatContent 和 fuelLoad 用于 Byram 强度)
        // ═══════════════════════════════════════════

        private struct FuelParams
        {
            public float baseSpreadRate;   // R0 (m/s) — 水平传播基础速率
            public float receptivity;      // 引燃易感性 (无单位)
            public float heatContent;      // H (kJ/kg) 低热值
            public float fuelLoad;         // w (kg/m²) 地表可燃物载量
            public float verticalBurnRate; // 火焰在树内垂直攀升速率 (高度比例/秒, 0~1)
        }

        private static readonly Dictionary<FuelType, FuelParams> FuelTable = new()
        {
            // 校准目标: 树间距~3m 时, 顺风条件下每棵树传播到邻居约需 30-60 秒。
            // R₀ 定义为 d=1m 基准速率, 实际传播会被风/坡度/距离等因子进一步缩放。
            // Softwood (针叶/松柏): 含油脂, 火焰攀升快, 树冠易爆燃 → ~20秒完全吞没
            // Hardwood (阔叶/硬木): 树皮厚含水高, 攀升慢但持续烧 → ~40秒完全吞没
            { FuelType.Grass,    new() { baseSpreadRate=0.12f, receptivity=1.5f, heatContent=18000f, fuelLoad=0.5f,  verticalBurnRate=0.12f } },
            { FuelType.Shrub,    new() { baseSpreadRate=0.08f, receptivity=1.2f, heatContent=19000f, fuelLoad=1.2f,  verticalBurnRate=0.06f } },
            { FuelType.Hardwood, new() { baseSpreadRate=0.04f, receptivity=0.8f, heatContent=20000f, fuelLoad=2.5f,  verticalBurnRate=0.025f } },
            { FuelType.Softwood, new() { baseSpreadRate=0.07f, receptivity=1.1f, heatContent=21000f, fuelLoad=2.0f,  verticalBurnRate=0.045f } },
            { FuelType.Litter,   new() { baseSpreadRate=0.18f, receptivity=1.8f, heatContent=16000f, fuelLoad=0.8f,  verticalBurnRate=0.02f } },
        };

        // ═══════════════════════════════════════════
        // 可调模型常数
        // ═══════════════════════════════════════════

        /// <summary>风因子系数。0.30 = 中等风速(3m/s)顺风时 φ_wind≈2.5x (温和)</summary>
        public static float windCoefficient     = 0.30f;
        /// <summary>坡度系数。1.2 = 温和坡度效应: 20°上坡≈1.5x, 30°≈1.9x, 下坡-20°≈0.65x</summary>
        public static float slopeCoefficient    = 1.2f;
        public static float distanceDecayRate   = 0.05f;
        public static float moistureExtinction  = 0.60f;
        public static float minWindCos          = -0.2f;

        /// <summary>辐射预干燥系数 η_rad，控制火线强度对邻居含水率的辐射干燥效率</summary>
        public static float dryingCoefficient   = 0.003f;

        /// <summary>预干燥有效范围 (m)，超过此距离不进行辐射干燥</summary>
        public static float preHeatRange        = 15f;

        /// <summary>垂直方向因子系数。1.0 = 温和烟囱效应 (原 3.0 太激进)</summary>
        public static float verticalCoefficient = 1.0f;
        /// <summary>垂直因子的下限值 (向下传播抑制)</summary>
        public static float verticalFactorMin   = 0.03f;
        /// <summary>垂直因子的上限值 (向上加速)</summary>
        public static float verticalFactorMax   = 20f;

        /// <summary>火焰高度系数。H_f = treeHeight × burnHeight × (0.5 + coeff × I)</summary>
        public static float flameHeightCoeff    = 0.008f;

        /// <summary>对流干燥系数 η_conv。热气柱对上方树木的含水率干燥效率</summary>
        public static float convectiveDryingCoeff = 0.002f;

        /// <summary>对流干燥有效范围 (m)</summary>
        public static float convectiveDryingRange = 25f;

        // ═══════════════════════════════════════════
        // 主入口 — 返回完整的 SpreadResult
        // ═══════════════════════════════════════════

        /// <summary>
        /// 核心传播计算。
        ///
        /// sourceBurnHeight: 源树火焰已攀升的高度比例 (0~1)。
        ///   火焰刚点燃的树 (burnHeight≈0) 辐射热有限;
        ///   完全吞没时 (burnHeight=1) 辐射最强。
        /// sourceIsTorching: 源树是否刚达到完全吞没 (torching 爆发态)。
        /// </summary>
        public static SpreadResult Compute(FireCell source, FireCell target,
            Vector3 windDirection, float windSpeed, float slopeAngle, float dt,
            float sourceBurnHeight = 1f, bool sourceIsTorching = false)
        {
            float dist   = Vector3.Distance(source.WorldPosition, target.WorldPosition);
            float r0     = GetBaseSpreadRate(source.fuelType);
            float φ_wind = ComputeWindFactor(windDirection, windSpeed, source.WorldPosition, target.WorldPosition, out float effWind);
            float φ_slope = ComputeSlopeFactor(slopeAngle);
            float φ_fuel = GetReceptivity(target.fuelType);
            float φ_moist = ComputeMoistureFactor(target.moisture);
            float φ_dist = ComputeDistanceFactor(dist);

            float dh     = target.WorldPosition.y - source.WorldPosition.y;
            float φ_vert = ComputeVerticalFactor(dh, dist);

            // 火焰高度因子: burnHeight 越高, 火焰柱越高, 辐射越强
            // 物理: 火焰辐射面积 ∝ 火焰高度, I ∝ flameHeight^0.5 (简化)
            float φ_flame = Mathf.Sqrt(Mathf.Max(0.05f, sourceBurnHeight));

            // Torching 爆发态: 树刚被完全吞没时, 瞬间热脉冲增强 3x
            float burstMul = sourceIsTorching ? 2f : 1f;

            float R = r0 * φ_wind * φ_slope * φ_fuel * φ_moist * φ_dist * φ_vert * φ_flame * burstMul;

            // Byram 火线强度 (也受火焰高度和爆发态影响)
            float I = ComputeFirelineIntensity(R, source.fuelType) * φ_flame * burstMul;

            // 火锋前进距离
            float advance = R * dt;

            // --- 火焰高度 (m) ---
            // H_f = treeH × burnH × (0.5 + flameHeightCoeff × I)
            // 物理: flameHeightCoeff 控制火线强度对火焰高度的贡献
            // I=50 → H_f ≈ treeH×burnH×0.9; I=200 → H_f ≈ treeH×burnH×2.1
            float treeH = source.treeHeight > 0f ? source.treeHeight : 10f;
            float flameHeight = treeH * sourceBurnHeight * (0.5f + flameHeightCoeff * I);
            flameHeight = Mathf.Max(flameHeight, 0.1f);

            // --- 直接点燃判断 ---
            // 火焰高度超过目标树冠底部高度 → 火焰可直接舔到目标枝叶
            float targetCrownBase = target.crownBaseHeightRatio > 0f
                ? target.TreeBaseY + target.crownBaseHeightRatio * (target.treeHeight > 0f ? target.treeHeight : 10f)
                : target.TreeBaseY + 0.4f * (target.treeHeight > 0f ? target.treeHeight : 10f); // 默认树冠底部在 40% 树高处
            float sourceBurnWorldY = source.TreeBaseY + flameHeight;
            bool canDirectIgnite = sourceBurnWorldY >= targetCrownBase && dist < flameHeight * 1.5f;

            // --- 辐射预干燥 ---
            float dryDelta = ComputePreHeatDrying(dist, I, dt);

            // --- 对流柱干燥 (仅目标在源上方时生效) ---
            float convDryDelta = ComputeConvectiveDrying(dh, dist, I, dt);

            return new SpreadResult
            {
                spreadRate            = R,
                firelineIntensity     = I,
                frontAdvance          = advance,
                flameHeight           = flameHeight,
                canDirectIgnite       = canDirectIgnite,
                baseSpreadRate        = r0,
                windFactor            = φ_wind,
                slopeFactor           = φ_slope,
                fuelFactor            = φ_fuel,
                moistureFactor        = φ_moist,
                distanceFactor        = φ_dist,
                verticalFactor        = φ_vert,
                flameHeightFactor     = φ_flame,
                preHeatMoistureDelta  = dryDelta,
                convectiveMoistureDelta = convDryDelta,
                effectiveWind         = effWind,
                slopeAngleDeg         = slopeAngle * Mathf.Rad2Deg,
                sourceToTargetDist    = dist,
            };
        }

        // ═══════════════════════════════════════════
        // Byram 火线强度 (改进点 1)
        // ═══════════════════════════════════════════

        /// <summary>
        /// Byram (1959) 火线强度: I_B = H × w × R
        /// I_B (kW/m): 单位火线长度释放的热功率
        /// </summary>
        public static float ComputeFirelineIntensity(float spreadRate, FuelType sourceFuel)
        {
            if (!FuelTable.TryGetValue(sourceFuel, out var p))
                return 0f;

            // I = H(kJ/kg) × w(kg/m²) × R(m/s) → kW/m
            return p.heatContent * p.fuelLoad * spreadRate * 0.001f;
        }

        /// <summary>
        /// 获取指定燃料类型的火焰垂直攀升速率 (高度比例/秒, 0~1)。
        /// 外部 (FireSpreadSystem) 用于每 Tick 推进 FireCell.burnHeight。
        /// </summary>
        public static float GetVerticalBurnRate(FuelType fuelType)
        {
            if (FuelTable.TryGetValue(fuelType, out var p))
                return p.verticalBurnRate;
            return 0.04f; // 默认 ~25秒 完全吞没
        }

        // ═══════════════════════════════════════════
        // 辐射预干燥 (改进点 2)
        // ═══════════════════════════════════════════

        /// <summary>
        /// 计算本 Tick 因辐射热导致的含水率下降量。
        ///
        /// 简化模型:
        ///   q_rad = I / (d² + 1)     (kW/m² 量级的辐射热通量)
        ///   ΔM   = η × q_rad × dt    (含水率变化)
        ///
        /// 超过 preHeatRange 时返回 0。
        /// </summary>
        public static float ComputePreHeatDrying(float distance, float firelineIntensity, float dt)
        {
            if (distance > preHeatRange || firelineIntensity <= 0f)
                return 0f;

            float qRad = firelineIntensity / (distance * distance + 1f);
            return dryingCoefficient * qRad * dt;
        }

        /// <summary>
        /// 对流柱干燥: 燃烧产生的热气流上升, 对上方树木的含水率产生额外干燥。
        ///
        /// 物理机制:
        ///   燃烧释放的热量形成上升气流 (对流柱/热柱), 温度可达 500-1000°C。
        ///   位于源树上方的可燃物被热气流持续烘烤, 含水率下降。
        ///
        /// 简化模型:
        ///   ΔM_conv = η_conv × I × exp(-d²/range²) × max(0, Δh/d) × dt
        ///
        ///   η_conv:     对流干燥系数
        ///   I:          火线强度 (kW/m)
        ///   exp(...):   距离衰减, 超出 convectiveDryingRange 忽略
        ///   max(0, Δh/d): 方向因子, 仅上方目标受益
        ///
        /// 仅当 target 高于 source 且距离在范围内时返回正值。
        /// </summary>
        public static float ComputeConvectiveDrying(float deltaHeight, float distance,
            float firelineIntensity, float dt)
        {
            // 仅对上方目标生效
            if (deltaHeight <= 0f) return 0f;
            if (distance > convectiveDryingRange || firelineIntensity <= 0f) return 0f;

            float distAtten = Mathf.Exp(-(distance * distance) / (convectiveDryingRange * convectiveDryingRange));
            float dirFactor = Mathf.Clamp01(deltaHeight / Mathf.Max(0.5f, distance));
            return convectiveDryingCoeff * firelineIntensity * distAtten * dirFactor * dt;
        }

        // ═══════════════════════════════════════════
        // 公开: 仅计算传播速率
        // ═══════════════════════════════════════════

        public static float ComputeSpreadRate(FireCell source, FireCell target,
            Vector3 windDirection, float windSpeed, float slopeAngle)
        {
            float r0       = GetBaseSpreadRate(source.fuelType);
            float φ_wind   = ComputeWindFactor(windDirection, windSpeed, source.WorldPosition, target.WorldPosition, out _);
            float φ_slope  = ComputeSlopeFactor(slopeAngle);
            float φ_fuel   = GetReceptivity(target.fuelType);
            float φ_moist  = ComputeMoistureFactor(target.moisture);
            float dist     = Vector3.Distance(source.WorldPosition, target.WorldPosition);
            float φ_dist   = ComputeDistanceFactor(dist);
            float φ_vert   = ComputeVerticalFactor(target.WorldPosition.y - source.WorldPosition.y, dist);
            float φ_flame  = Mathf.Sqrt(Mathf.Max(0.05f, source.BurnHeight));

            return r0 * φ_wind * φ_slope * φ_fuel * φ_moist * φ_dist * φ_vert * φ_flame;
        }

        // ═══════════════════════════════════════════
        // 因子计算 (不变)
        // ═══════════════════════════════════════════

        private static float ComputeWindFactor(Vector3 windDir, float windSpeed,
            Vector3 sourcePos, Vector3 targetPos, out float effectiveWind)
        {
            Vector3 toTarget = (targetPos - sourcePos).normalized;
            float cosTheta = Vector3.Dot(windDir, toTarget);
            cosTheta = Mathf.Max(cosTheta, minWindCos);
            effectiveWind = windSpeed * cosTheta;
            return Mathf.Exp(windCoefficient * effectiveWind);
        }

        /// <summary>
        /// 坡度因子 φ_slope = exp(slopeCoefficient × tan(β))
        ///
        /// 双向坡度效应：
        ///   tan(β) > 0 (上坡): φ > 1 → 加速 (火焰向上坡预热)
        ///   tan(β) < 0 (下坡): φ < 1 → 抑制 (热量向上散逸,不易向下传递)
        ///
        /// 下坡方向钳制在 -0.6 以上 (约 −31°)，防止极端悬崖地形完全阻断传播。
        /// </summary>
        private static float ComputeSlopeFactor(float slopeAngleRad)
        {
            float tanBeta = Mathf.Tan(slopeAngleRad);
            float betaEff = Mathf.Clamp(tanBeta, -0.6f, 10f);
            return Mathf.Exp(slopeCoefficient * betaEff);
        }

        private static float ComputeMoistureFactor(float moisture)
        {
            float ratio = moisture / moistureExtinction;
            return Mathf.Max(0.01f, 1f - ratio);
        }

        private static float ComputeDistanceFactor(float dist)
        {
            if (dist <= 0.01f) dist = 0.01f;
            return Mathf.Exp(-distanceDecayRate * dist);
        }

        /// <summary>
        /// 垂直方向传播因子 φ_vertical。
        ///
        /// 物理基础：火焰和热气流自然上升（烟囱效应），向上传播显著快于向下。
        ///   Δh = target.y − source.y (正值 = 目标在上方)
        ///
        ///   φ_vertical = exp(verticalCoefficient × Δh / dist)
        ///
        ///   Δh > 0 (向上): φ > 1 → 加速 (热气流预热上方可燃物)
        ///   Δh < 0 (向下): φ < 1 → 抑制 (热量向上散逸，不易向下传递)
        ///
        /// 结果钳制在 [verticalFactorMin, verticalFactorMax] 内。
        /// </summary>
        private static float ComputeVerticalFactor(float deltaHeight, float distance)
        {
            if (distance < 0.01f) distance = 0.01f;
            float ratio = deltaHeight / distance;
            float phi = Mathf.Exp(verticalCoefficient * ratio);
            return Mathf.Clamp(phi, verticalFactorMin, verticalFactorMax);
        }

        private static float GetBaseSpreadRate(FuelType ft)
            => FuelTable.TryGetValue(ft, out var p) ? p.baseSpreadRate : 0.3f;

        private static float GetReceptivity(FuelType ft)
            => FuelTable.TryGetValue(ft, out var p) ? p.receptivity : 1.0f;
    }
}
