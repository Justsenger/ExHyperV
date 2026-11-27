using ExHyperV.Models;
using R = ExHyperV.Properties.Resources;

namespace ExHyperV.Services;

public sealed partial class GpuDeploymentSession
{
    private void SetDriverStepAsSelectPartition()
    {
        int driverIdx = FindStepIndex(GpuDeploymentStepKind.Driver);
        if (driverIdx >= 0)
            steps[driverIdx] = steps[driverIdx] with { Name = R.Task_Gpu_SelectPartition };
    }

    private void SetDriverStepAsWindowsInstall()
    {
        int driverIdx = FindStepIndex(GpuDeploymentStepKind.Driver);
        if (driverIdx >= 0)
            steps[driverIdx] = steps[driverIdx] with { Name = R.Task_Gpu_Driver };
    }

    private int FindStepIndex(GpuDeploymentStepKind kind)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Type == kind) return i;
        }
        return -1;
    }

    private void TrimLinuxStepsFrom(int startIndex)
    {
        if (startIndex >= 0 && startIndex < steps.Count)
            steps.RemoveRange(startIndex, steps.Count - startIndex);
    }

    private void HandleBackToPartitions()
    {
        selected = null;
        bundleError = null;
        stepProgressPercent = null;
        guestSetupCommand = null;
        linuxIsoPath = null;
        linuxDriverVersion = null;

        int driverIdx = FindStepIndex(GpuDeploymentStepKind.Driver);
        if (driverIdx >= 0)
        {
            TrimLinuxStepsFrom(driverIdx + 1);
            index = driverIdx;
            SetDriverStepAsSelectPartition();
        }
        Ask(GpuDecisionKind.SelectPartition, R.Label_SelectPart, BuildPartitionSelectMessageForCurrentPartitions());
    }

    private string BuildPartitionSelectMessageForCurrentPartitions()
    {
        var all = partitions.Values.ToList();
        int usableCount = all.Count(IsSupported);
        bool hasBitLocker = all.Any(p => p.IsBitLocker);
        bool hasLinux = all.Any(p => p.OsType == OperatingSystemType.Linux);
        return BuildPartitionSelectMessage(usableCount, hasLinux, hasBitLocker);
    }

    private void EnterLinuxBundleSelection(string? recoverableError = null)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        stepProgressPercent = null;
        guestSetupCommand = null;
        linuxIsoPath = null;
        linuxDriverVersion = null;
        bundleError = recoverableError;

        int driverIdx = FindStepIndex(GpuDeploymentStepKind.Driver);
        if (driverIdx < 0) throw new InvalidOperationException();

        SetDriverStepAsSelectPartition();
        steps[driverIdx] = steps[driverIdx] with
        {
            Status = GpuDeploymentStepStatus.Success,
            Description = selected?.DisplayName ?? steps[driverIdx].Description
        };

        TrimLinuxStepsFrom(driverIdx + 1);
        steps.Add(new GpuDeploymentStep(
            GpuDeploymentStepKind.SelectBundle,
            R.Task_Gpu_SelectBundle,
            R.Msg_Gpu_SelectLinuxBundle,
            GpuDeploymentStepStatus.Running));
        index = driverIdx + 1;

        Ask(GpuDecisionKind.SelectLinuxBundle,
            R.Label_SelectLinuxBundle,
            recoverableError ?? R.Msg_Gpu_SelectLinuxBundle,
            canGoBack: true);
    }

    private async Task ExecuteLinuxBundleAsync(GpuLinuxBundleSelection bundleChoice)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        await VerifyTargetAsync();

        bundleError = null;
        stepProgressPercent = null;
        bool online = bundleChoice.Source == GpuLinuxBundleSource.Online;
        string? localZip = bundleChoice.LocalBundlePath?.Trim();

        int bundleIdx = FindStepIndex(GpuDeploymentStepKind.SelectBundle);
        if (bundleIdx < 0) throw new InvalidOperationException();

        steps[bundleIdx] = steps[bundleIdx] with
        {
            Status = GpuDeploymentStepStatus.Success,
            Description = online ? R.Msg_Gpu_LinuxBundleOnline : Path.GetFileName(localZip ?? string.Empty)
        };
        TrimLinuxStepsFrom(bundleIdx + 1);

        if (online)
            steps.Add(new GpuDeploymentStep(GpuDeploymentStepKind.DownloadBundle, R.Task_Gpu_DownloadBundle, R.Msg_Gpu_FetchingBundleInfo, GpuDeploymentStepStatus.Pending));
        steps.Add(new GpuDeploymentStep(GpuDeploymentStepKind.SynthesizeBundle, R.Task_Gpu_SynthesizeBundle, R.Msg_Gpu_LinuxSynthesizing, GpuDeploymentStepStatus.Pending));
        steps.Add(new GpuDeploymentStep(GpuDeploymentStepKind.MountIso, R.Task_Gpu_MountIso, R.Msg_Gpu_LinuxMountingIso, GpuDeploymentStepStatus.Pending));

        index = bundleIdx + 1;
        Publish(steps[bundleIdx].Description);

        string resolvedZipPath = localZip ?? string.Empty;
        try
        {
            if (online)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await VerifyTargetAsync();
                SetStep(GpuDeploymentStepStatus.Running, R.Msg_Gpu_FetchingBundleInfo, R.Msg_Gpu_FetchingBundleInfo);
                resolvedZipPath = await backend.DownloadLinuxBundleAsync(
                    (pct, desc) => SetStepProgress(pct, desc),
                    cancellation.Token);
                stepProgressPercent = null;
                Succeed(R.Msg_Gpu_LinuxBundleOnline);
                index++;
            }

            cancellation.Token.ThrowIfCancellationRequested();
            await VerifyTargetAsync();
            SetStep(GpuDeploymentStepStatus.Running, R.Msg_Gpu_LinuxSynthesizing, R.Msg_Gpu_LinuxSynthesizing);
            string isoPath = await backend.SynthesizeLinuxIsoAsync(
                resolvedZipPath,
                (pct, desc) => SetStepProgress(pct, desc),
                cancellation.Token);
            stepProgressPercent = null;
            Succeed(Path.GetFileName(isoPath));
            index++;

            cancellation.Token.ThrowIfCancellationRequested();
            await VerifyTargetAsync();
            SetStep(GpuDeploymentStepStatus.Running, R.Msg_Gpu_LinuxMountingIso, R.Msg_Gpu_LinuxMountingIso);
            await backend.MountLinuxIsoAsync(isoPath, cancellation.Token);
            Succeed(R.Msg_Gpu_LinuxIsoMounted);
            index++;

            await backend.RecordGuestDriverVersionsAsync();
            guestSetupCommand = LinuxGpuPvBundleService.GuestSetupCommand;
            linuxIsoPath = isoPath;
            linuxDriverVersion = backend.GetPrimaryDriverVersion();
            Publish(R.Msg_Gpu_LinuxIsoMounted);
        }
        catch (LinuxBundleException ex) when (!cancellation.IsCancellationRequested)
        {
            EnterLinuxBundleSelection(ex.Message);
        }
    }

    private void SetStepProgress(int percent, string description)
    {
        stepProgressPercent = Math.Clamp(percent, 0, 100);
        if (index < steps.Count)
            steps[index] = steps[index] with { Status = GpuDeploymentStepStatus.Running, Description = description };
        Publish(description);
    }
}
