using ExHyperV.Core.Tests.ResourceInspection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class PacketCaptureAvailabilityTests
{
    [TestMethod]
    [DataRow("\r\n数据包监视器没有运行。", 0, "数据包筛选器:\r\n    无", 0, true)]
    [DataRow("Packet Monitor is not running.", 0, "Packet Filters:\r\n    None", 0, true)]
    [DataRow("已停止", 0, "没有筛选器", 0, true)]
    [DataRow("Stopped", 0, "No filters configured.", 0, true)]
    [DataRow("数据包监视器正在运行。", 0, "数据包筛选器:\r\n    无", 0, false)]
    [DataRow("Packet Monitor is not stopped.", 0, "No filters", 0, false)]
    [DataRow("数据包监视器没有运行。", 0, "数据包筛选器:\r\n    ARP", 0, false)]
    [DataRow("Stopped", 0, "Packet Filters:\r\n    1 no filters", 0, false)]
    [DataRow("Stopped", 1, "No filters", 0, false)]
    [DataRow("Stopped", 0, "No filters", 1, false)]
    [DataRow("", 0, "", 0, false)]
    public void OnlyConfirmedStoppedCaptureWithEmptyFiltersIsAvailable(string status, int code, string filters, int filterCode, bool expected)
        => Assert.AreEqual(expected, PacketCaptureAvailability.IsIdle(status, code, filters, filterCode));
}
