namespace ExHyperV.Services;
    /// <summary>虚拟机 MMIO 空间与当前主机分配要求的比较结果。</summary>
    public enum MmioCheckResultType
    {
        /// <summary>当前空间达到要求。</summary>
        Ok,
        /// <summary>当前空间不足，需要扩展。</summary>
        NeedsExpansion
    }
