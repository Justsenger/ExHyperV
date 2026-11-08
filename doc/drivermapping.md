# Hyper-V GPU-P Driver Mapping Table

This table describes the symbolic link (mklink) relationships used to inject host GPU drivers into a Guest VM. 

**Note:** ExHyperV mirrors the host's complete `DriverStore\FileRepository` into the guest. The exact package selection below is still required: it limits registry promotion and symbolic-link sources to the packages belonging to the selected physical adapter, so a same-named file from an older driver or another GPU is never chosen accidentally.

## Driver package selection

Package selection happens before any file mapping:

1. Read `InfPath` from the selected adapter's display-class registry key.
2. Resolve that published `oem*.inf` through SetupAPI to the exact active package directory in `DriverStore\FileRepository`; directory-name and version-string guessing are not used.
3. Mirror the complete host `FileRepository`, preserving all package subdirectories. Windows and Linux deployments use this same resolved source root. This deliberately favors deployment completeness and future package layouts over copy size.
4. `CopyINF` is still parsed for source scoping, not to decide which directories get copied. Many entries are separate audio, PCI, I2C or platform devices which GPU-PV does not install. At present AMD's promotion layer consumes `amdocl.inf` (OpenCL/HIP) and `amdwin-*.inf` (Windows support components). Their `DriverVer` must match the selected display INF exactly. A missing or ambiguous optional companion is omitted with a warning; the base GPU deployment continues without mixing driver generations.

Registry-defined `CopyToVm*` sources are restricted to the active display package. Fixed vendor mappings are restricted to the selected package set. AMD OpenCL/HIP mappings are further restricted to the selected `amdocl.inf` package, and AMD Windows-support mappings to the selected `amdwin-*.inf` package. Existing unrelated or older packages in the guest never participate as fallback sources.

For NVIDIA, ExHyperV creates or updates only the offline guest's minimal `nvlddmkm` kernel-service bootstrap: `Type`, `Start`, `ErrorControl`, `Group`, and `ImagePath`. `ImagePath` is built from the exactly selected display package and points to its `nvlddmkm.sys` below `System32\HostDriverStore\FileRepository`. The host's complete `nvlddmkm` service tree is not imported; host PnP bindings (`Enum`), volatile state (`State`), and installed feature-state subkeys do not belong to the guest.

After driver promotion, ExHyperV also copies the selected vendor's existing software/data directory trees in full. NVIDIA covers `NVIDIA Corporation` and `NVIDIA`; AMD covers `AMD` and legacy `ATI Technologies`; Intel covers `Intel` and `Intel Corporation`; Qualcomm covers `Qualcomm` and `Qualcomm Incorporated`. Each name is checked below `Program Files`, `Program Files (x86)`, and `ProgramData`; absent directories are skipped. This preserves file-based runtime consumers such as NVIDIA PhysX/game components, but copying files alone does not register a Store application, service, scheduled task, or COM server that was never installed in the guest.

This document describes deployment mappings only. ExHyperV does not perform runtime API or feature validation after deployment.

Driver catalog files remain inside the copied `HostDriverStore`. No vendor path creates links named `oemNN.cat`: that number belongs to a particular guest DriverStore database and cannot be inferred from a host GPU package or hard-coded across guests.

## Registry-defined mappings

Before applying the fixed vendor mappings below, ExHyperV processes the Microsoft GPU-PV promotion rules from the selected display adapter's registry key. Source paths are resolved relative to that adapter's active DriverStore package, including package subdirectories such as `B026261\`.

| Registry Subkey | Guest Target Directory | Replacement Rule |
| :--- | :--- | :--- |
| CopyToVmOverwrite | System32 | Always replace the destination |
| CopyToVmWhenNewer | System32 | For DLL/EXE files, compare FileVersion first; when equal, compare LastWriteTime |
| CopyToVmOverwriteWow64 | SysWOW64 | Always replace the destination |
| CopyToVmWhenNewerWow64 | SysWOW64 | For DLL/EXE files, compare FileVersion first; when equal, compare LastWriteTime |

The current AMD display package declares these six mappings dynamically. `Bxxxxxx` is driver-version-specific and is read from the registry rather than hard-coded:

| Host Source File | Guest Target Directory | Guest Target Filename |
| :--- | :--- | :--- |
| Bxxxxxx\amfrt64.dll | System32 | amfrt64.dll |
| Bxxxxxx\atio6axx.dll | System32 | atio6axx.dll |
| Bxxxxxx\amd_opencl64.dll | System32 | OpenCL.dll |
| Bxxxxxx\amfrt32.dll | SysWOW64 | amfrt32.dll |
| Bxxxxxx\atioglxx.dll | SysWOW64 | atioglxx.dll |
| Bxxxxxx\amd_opencl32.dll | SysWOW64 | OpenCL.dll |

---

## 1. NVIDIA

### System32 (64-bit Core)
| Host Source File | Guest Target Directory | Guest Target Filename |
| :--- | :--- | :--- |
| MCU.exe | System32 | MCU.exe |
| nvapi64.dll | System32 | nvapi64.dll |
| nvcpl.dll | System32 | nvcpl.dll |
| nvcuda_loader64.dll | System32 | nvcuda.dll |
| nvcudadebugger.dll | System32 | nvcudadebugger.dll |
| nvcuvid64.dll | System32 | nvcuvid.dll |
| nvdebugdump.exe | System32 | nvdebugdump.exe |
| nvEncodeAPI64.dll | System32 | nvEncodeAPI64.dll |
| NvFBC64.dll | System32 | NvFBC64.dll |
| nvidia-pcc.exe | System32 | nvidia-pcc.exe |
| nvidia-smi.exe | System32 | nvidia-smi.exe |
| NvIFR64.dll | System32 | NvIFR64.dll |
| nvinfo.pb | System32 | nvinfo.pb |
| nvml_loader.dll | System32 | nvml.dll |
| nvofapi64.dll | System32 | nvofapi64.dll |
| OpenCL64.dll | System32 | OpenCL.dll |
| vulkan-1-x64.dll | System32 | vulkan-1.dll |
| vulkan-1-x64.dll | System32 | vulkan-1-999-0-0-0.dll |
| vulkaninfo-x64.exe | System32 | vulkaninfo.exe |
| license.txt | System32\drivers\NVIDIA Corporation | license.txt |
| dbInstaller.exe | System32\drivers\NVIDIA Corporation\Drs | dbInstaller.exe |
| nvdrsdb.bin | System32\drivers\NVIDIA Corporation\Drs | nvdrsdb.bin |

### System32\lxss (WSL Support)
| Host Source File | Guest Target Directory | Guest Target Filename |
| :--- | :--- | :--- |
| libcuda_loader.so | System32\lxss\lib | libcuda.so |
| libcuda_loader.so | System32\lxss\lib | libcuda.so.1 |
| libcuda_loader.so | System32\lxss\lib | libcuda.so.1.1 |
| libcudadebugger.so.1 | System32\lxss\lib | libcudadebugger.so.1 |
| libnvcuvid.so.1 | System32\lxss\lib | libnvcuvid.so |
| libnvcuvid.so.1 | System32\lxss\lib | libnvcuvid.so.1 |
| libnvdxdlkernels.so | System32\lxss\lib | libnvdxdlkernels.so |
| libnvidia-encode.so.1 | System32\lxss\lib | libnvidia-encode.so |
| libnvidia-encode.so.1 | System32\lxss\lib | libnvidia-encode.so.1 |
| libnvidia-ml_loader.so | System32\lxss\lib | libnvidia-ml.so.1 |
| libnvidia-ngx.so.1 | System32\lxss\lib | libnvidia-ngx.so.1 |
| libnvidia-opticalflow.so.1 | System32\lxss\lib | libnvidia-opticalflow.so |
| libnvidia-opticalflow.so.1 | System32\lxss\lib | libnvidia-opticalflow.so.1 |
| libnvoptix_loader.so.1 | System32\lxss\lib | libnvoptix.so.1 |
| libnvwgf2umx.so | System32\lxss\lib | libnvwgf2umx.so |
| nvidia-ngx-updater | System32\lxss\lib | nvidia-ngx-updater |
| nvidia-smi | System32\lxss\lib | nvidia-smi |

### SysWOW64 (32-bit Compatibility)
| Host Source File | Guest Target Directory | Guest Target Filename |
| :--- | :--- | :--- |
| nvapi.dll | SysWOW64 | nvapi.dll |
| nvcuda_loader32.dll | SysWOW64 | nvcuda.dll |
| nvcuvid32.dll | SysWOW64 | nvcuvid.dll |
| nvEncodeAPI.dll | SysWOW64 | nvEncodeAPI.dll |
| NvFBC.dll | SysWOW64 | NvFBC.dll |
| NvIFR.dll | SysWOW64 | NvIFR.dll |
| nvofapi.dll | SysWOW64 | nvofapi.dll |
| OpenCL32.dll | SysWOW64 | OpenCL.dll |
| vulkan-1-x86.dll | SysWOW64 | vulkan-1.dll |
| vulkan-1-x86.dll | SysWOW64 | vulkan-1-999-0-0-0.dll |
| vulkaninfo-x86.exe | SysWOW64 | vulkaninfo.exe |

---

## 2. Intel

### System32 (64-bit)
| Host Source File | Guest Target Directory | Guest Target Filename |
| :--- | :--- | :--- |
| ControlLib.dll | System32 | ControlLib.dll |
| intel_gfx_api-x64.dll | System32 | intel_gfx_api-x64.dll |
| mfx_loader_dll_hw64.dll | System32 | libmfxhw64.dll |
| vpl_dispatcher_64.dll | System32 | libvpl.dll |
| mfxplugin64_hw.dll | System32 | mfxplugin64_hw.dll |
| vulkan-1-64.dll | System32 | vulkan-1.dll |
| vulkan-1-64.dll | System32 | vulkan-1-999-0-0-0.dll |
| vulkaninfo-64.exe | System32 | vulkaninfo.exe |
| vulkaninfo-64.exe | System32 | vulkaninfo-1-999-0-0-0.exe |
| ze_intel_gpu_raytracing.dll | System32 | ze_intel_gpu_raytracing.dll |
| ze_loader.dll | System32 | ze_loader.dll |
| ze_tracing_layer.dll | System32 | ze_tracing_layer.dll |
| ze_validation_layer.dll | System32 | ze_validation_layer.dll |

### SysWOW64 (32-bit)
| Host Source File | Guest Target Directory | Guest Target Filename |
| :--- | :--- | :--- |
| ControlLib32.dll | SysWOW64 | ControlLib32.dll |
| IntelControlLib32.dll | SysWOW64 | IntelControlLib32.dll |
| intel_gfx_api-x86.dll | SysWOW64 | intel_gfx_api-x86.dll |
| mfx_loader_dll_hw32.dll | SysWOW64 | libmfxhw32.dll |
| vpl_dispatcher_32.dll | SysWOW64 | libvpl.dll |
| mfxplugin32_hw.dll | SysWOW64 | mfxplugin32_hw.dll |
| vulkan-1-32.dll | SysWOW64 | vulkan-1.dll |
| vulkan-1-32.dll | SysWOW64 | vulkan-1-999-0-0-0.dll |
| vulkaninfo-32.exe | SysWOW64 | vulkaninfo.exe |
| vulkaninfo-32.exe | SysWOW64 | vulkaninfo-1-999-0-0-0.exe |

### Intel runtime registry compatibility

The Intel file mappings above are only the binary-promotion layer. ExHyperV also copies the application-facing runtime values from the **selected GPU-PV adapter's** display-class key into the offline guest SYSTEM hive. It preserves the registry value type and rewrites only `System32\DriverStore` paths to `System32\HostDriverStore`.

The selected adapter is resolved from its `GPUPARAV` instance path through `Enum\<device>\Driver`; the display-class number is not hard-coded. Windows exposes that host adapter registry path to Intel NEO through `KMTQAITYPE_UMDRIVERPRIVATE`, so the same numbered guest class key is populated before the virtual render device starts. The copied values cover the installed driver's OpenCL, Level Zero, OpenGL, Vulkan, VPL/MDF, content-protection, and Intel Control API registrations when those values exist. Physical-device D3D UMD, WSL, and Android-cabinet registrations are deliberately left to the virtual render stack rather than copied wholesale.

Intel's Windows Level Zero loader discovers drivers by enumerating present display DEVNODEs and reading `LevelZeroDriverPath` from each device software key. The GPU-PV virtual render device software key is first created at guest boot, after ExHyperV's offline deployment. To make Level Zero available on that first boot without a logon script or a second deployment pass, ExHyperV additionally places the selected Intel path on the already-installed Microsoft Hyper-V Video display key. This adds one discovery entry without setting `ZE_ENABLE_ALT_DRIVERS` and therefore does not disable discovery of other display or compute-accelerator drivers.

---

## 3. AMD

### System32 (64-bit)
| Host Source File | Guest Target Directory | Guest Target Filename |
| :--- | :--- | :--- |
| atidxxstub64.dll | System32 | atidxx64.dll |
| amdxcstub64.dll | System32 | amdxc64.dll |
| amdxc64.so | System32 | amdxc64.so |
| amdadlx64.dll | System32 | amdadlx64.dll |
| amdave64.dll | System32 | amdave64.dll |
| amdgfxinfo64.dll | System32 | amdgfxinfo64.dll |
| amdlvr64.dll | System32 | amdlvr64.dll |
| amdpcom64.dll | System32 | amdpcom64.dll |
| amfrt64.dll | System32 | amfrt64.dll |
| atiadlxx.dll | System32 | atiadlxx.dll |
| atimpc64.dll | System32 | atimpc64.dll |
| atisamu64.dll | System32 | atisamu64.dll |
| amdsasrv64.dll | System32 | amdsasrv64.dll |
| amdsacli64.dll | System32 | amdsacli64.dll |
| atieclxx.exe | System32 | atieclxx.exe |
| atieah64.exe | System32 | atieah64.exe |
| EEURestart.exe | System32 | EEURestart.exe |
| GameManager64.dll | System32 | GameManager64.dll |
| amdmiracast.dll | System32 | amdmiracast.dll |
| amf-mft-mjpeg-decoder64.dll | System32 | amf-mft-mjpeg-decoder64.dll |
| atidemgy.dll | System32 | atidemgy.dll |
| atimuixx.dll | System32 | atimuixx.dll |
| atiapfxx.blb | System32 | atiapfxx.blb |
| ativvsva.dat | System32 | ativvsva.dat |
| ativvsvl.dat | System32 | ativvsvl.dat |
| AMDKernelEvents.mc | System32 | AMDKernelEvents.man |
| detoured64.dll | System32 | detoured.dll |
| vulkan64.dll | System32 | vulkan-1.dll |
| vulkan64.dll | System32 | vulkan-1-999-0-0-0.dll |
| vulkaninfo64.exe | System32 | vulkaninfo.exe |
| vulkaninfo64.exe | System32 | vulkaninfo-1-999-0-0-0.exe |
| amd_comgr_2.dll | System32 | amd_comgr_2.dll |
| amdhip64_6.dll | System32 | amdhip64_6.dll |
| amdmmcl.dll | System32 | amdmmcl.dll |
| amdmmcl6.dll | System32 | amdmmcl6.dll |
| clinfo.exe | System32 | clinfo.exe |
| hiprt02000_amd.hipfb | System32 | hiprt02000_amd.hipfb |
| hiprt0200064.dll | System32 | hiprt0200064.dll |
| oro_compiled_kernels.hipfb | System32 | oro_compiled_kernels.hipfb |
| amdlogum.exe | System32 | amdlogum.exe |
| dgtrayicon.exe | System32 | dgtrayicon.exe |
| Rapidfire64.dll | System32 | Rapidfire64.dll |
| RapidFireServer64.dll | System32 | RapidFireServer64.dll |

### SysWOW64 (32-bit)
| Host Source File | Guest Target Directory | Guest Target Filename |
| :--- | :--- | :--- |
| atidxxstub32.dll | SysWOW64 | atidxx32.dll |
| amdxcstub32.dll | SysWOW64 | amdxc32.dll |
| amdadlx32.dll | SysWOW64 | amdadlx32.dll |
| amdave32.dll | SysWOW64 | amdave32.dll |
| amdgfxinfo32.dll | SysWOW64 | amdgfxinfo32.dll |
| amdlvr32.dll | SysWOW64 | amdlvr32.dll |
| amdpcom32.dll | SysWOW64 | amdpcom32.dll |
| amfrt32.dll | SysWOW64 | amfrt32.dll |
| atimpc32.dll | SysWOW64 | atimpc32.dll |
| atisamu32.dll | SysWOW64 | atisamu32.dll |
| GameManager32.dll | SysWOW64 | GameManager32.dll |
| atiadlxy.dll | SysWOW64 | atiadlxx.dll |
| amdsacli32.dll | SysWOW64 | amdsacli32.dll |
| amf-mft-mjpeg-decoder32.dll | SysWOW64 | amf-mft-mjpeg-decoder32.dll |
| atiadlxy.dll | SysWOW64 | atiadlxy.dll |
| atieah32.exe | SysWOW64 | atieah32.exe |
| detoured32.dll | SysWOW64 | detoured.dll |
| atiapfxx.blb | SysWOW64 | atiapfxx.blb |
| ativvsva.dat | SysWOW64 | ativvsva.dat |
| ativvsvl.dat | SysWOW64 | ativvsvl.dat |
| vulkan32.dll | SysWOW64 | vulkan-1.dll |
| vulkan32.dll | SysWOW64 | vulkan-1-999-0-0-0.dll |
| vulkaninfo32.exe | SysWOW64 | vulkaninfo.exe |
| vulkaninfo32.exe | SysWOW64 | vulkaninfo-1-999-0-0-0.exe |
| amd_comgr32.dll | SysWOW64 | amd_comgr32.dll |
| Rapidfire.dll | SysWOW64 | Rapidfire.dll |
| RapidFireServer.dll | SysWOW64 | RapidFireServer.dll |

### AMD companion-package selection

The OpenCL/HIP files are selected from the `amdocl.inf_amd64_*` package whose INF is declared by the active display INF and whose normalized `DriverVer` exactly matches the active display INF. AMDWIN files are selected the same way: the exact `amdwin-*.inf` name comes from the selected display INF's `CopyINF` directive (for example, `amdwin-u0202642.inf`), rather than being inferred from a package-directory name or timestamp. If a declared companion does not have exactly one same-version match, it is omitted and its mappings produce warnings; the base deployment continues. A companion not declared by the display INF is also omitted rather than selecting an unrelated package.

RapidFire and `hiprt0200064.dll` import the Microsoft Visual C++ runtime. ExHyperV promotes the AMD-owned files but does not install or replace the machine-wide VC++ runtime; applications which require these optional components must satisfy that Microsoft prerequisite separately.

### AMD MFT registry behavior

ExHyperV does not synthesize values inside an offline GPU-PV display-class instance. Windows creates or reuses the active `PCI\VEN_1414&DEV_008E` class instance on the next guest boot, and stale instances can remain after a GPU partition is removed and assigned again. Therefore an offline deployment cannot reliably identify the class instance which will become active.

Validation showed the same 7 video encoders and 11 video decoders before and after adding the four DDA-only class values (`MFTFlags`, `OutputTypes`, and two `EnableDecoders` values). They are not required for the verified GPU-PV MFT functionality. The required 32-bit and 64-bit MJPEG decoder DLLs are promoted by the file mappings above; those file mappings changed the 32-bit MJPEG load result from failure to success.

---

## 4. Qualcomm (QCOM)

Qualcomm follows the same deployment flow as NVIDIA, AMD, and Intel. ExHyperV first copies the host driver package into `HostDriverStore`, then applies the registry-defined mappings and the vendor mappings below as best-effort compatibility promotions. A missing or ambiguous promotion is reported as a warning and does not block the base GPU deployment.

The tables below list the fixed compatibility promotions used with the hardware-validated `qcdx8380.inf` version 31.0.152.1. Existing regular guest files are preserved by default; the Vulkan loader and tools use the `WhenNewer` replacement rule.

### System32 (native ARM64)

| Host Source File | Guest Target Directory | Guest Target Filename |
| :--- | :--- | :--- |
| qchdcpumd8380.dll | System32 | qchdcpumd8380.dll |
| qcdxkmsuc8380.mbn | System32 | qcdxkmsuc8380.mbn |
| qcdxkmsucpurwa.mbn | System32 | qcdxkmsucpurwa.mbn |
| OpenCL.dll | System32 | OpenCL.dll |
| vulkan-1.dll | System32 | vulkan-1.dll |
| vulkan-1.dll | System32 | vulkan-1-999-0-0-0.dll |
| vulkaninfo.exe | System32 | vulkaninfo.exe |
| vulkaninfo.exe | System32 | vulkaninfo-1-999-0-0-0.exe |

### SysWOW64 (x86 compatibility)

| Host Source File | Guest Target Directory | Guest Target Filename |
| :--- | :--- | :--- |
| qcdx11x86um.dll | SysWOW64 | qcdx11x86um.dll |
| qcdxdmlx86.dll | SysWOW64 | qcdxdmlx86.dll |
| qcdx12x86um.dll | SysWOW64 | qcdx12x86um.dll |
| qcgpux86compilercore.DLL | SysWOW64 | qcgpux86compilercore.DLL |
| qcvidencx86um.DLL | SysWOW64 | qcvidencum.DLL |
| qcdxsdx86.dll | SysWOW64 | qcdxsdx86.dll |
| qcegpx86.dll | SysWOW64 | qcegpx86.dll |

### SyChpe32 (CHPE emulation)

| Host Source File | Guest Target Directory | Guest Target Filename |
| :--- | :--- | :--- |
| qcdx11chpeum.dll | SyChpe32 | qcdx11x86um.dll |
| qcdxdmlchpe.dll | SyChpe32 | qcdxdmlx86.dll |
| qcdx12chpeum.dll | SyChpe32 | qcdx12x86um.dll |
| qcgpuchpecompilercore.dll | SyChpe32 | qcgpux86compilercore.DLL |
| qcdxsdchpe.dll | SyChpe32 | qcdxsdx86.dll |
| qcegpchpe.dll | SyChpe32 | qcegpx86.dll |

The former hard-coded `qcdx8380.cat -> oem7.cat` promotion has been removed, and the CHPE mapping for `qcegpchpe.dll` now correctly targets `qcegpx86.dll`.

Validation covered ARM64 Direct3D 11/12, Vulkan 1.3, OpenCL 3.0 and DirectML; x86/CHPE Direct3D 11/12 and DirectML; and GPU performance counters used by ExHyperV monitoring.
