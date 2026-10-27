namespace FireSimulation
{
    /// <summary>
    /// FireCell 的燃烧状态
    /// </summary>
    public enum FireCellState
    {
        Idle,       // 健康/未点燃
        Burning,    // 正在燃烧
        Burned      // 已烧尽
    }

    /// <summary>
    /// 可燃物类型，影响基础传播速率和燃点温度
    /// </summary>
    public enum FuelType
    {
        Grass,      // 草地 — 极易点燃，燃烧快
        Shrub,      // 灌木 — 中等
        Hardwood,   // 硬木/阔叶 — 难点燃，燃烧慢
        Softwood,   // 软木/针叶 — 含油脂，易点燃
        Litter      // 地表枯枝落叶 — 极易点燃，火势猛
    }
}
