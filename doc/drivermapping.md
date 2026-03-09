# Hyper-V GPU-P Driver Mapping Table

This table describes the symbolic link (mklink) relationships used to inject host GPU drivers into a Guest VM. 

**Note:** The "Host Source File" is typically located within the host's `C:\Windows\System32\DriverStore\FileRepository\` directory.

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
| NV_DISP.CAT | System32\CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE} | oem25.cat |
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
| igdlh.cat | System32\CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE} | oem95.cat |
| igdlh.cat | System32\CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE} | oem108.cat |

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
| atiapfxx.blb | System32 | atiapfxx.blb |
| ativvsva.dat | System32 | ativvsva.dat |
| ativvsvl.dat | System32 | ativvsvl.dat |
| AMDKernelEvents.mc | System32 | AMDKernelEvents.man |
| detoured64.dll | System32 | detoured.dll |
| amdkmpfd.ctz | System32\AMD\amdkmpfd | amdkmpfd.ctz |
| amdkmpfd.itz | System32\AMD\amdkmpfd | amdkmpfd.itz |
| amdkmpfd.stz | System32\AMD\amdkmpfd | amdkmpfd.stz |
| u0418637.cat | System32\CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE} | oem43.cat |
| amdvlk64.dll | System32 | amdvlk64.dll |
| amdvlk64.dll | System32 | vulkan-1.dll |

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
| detoured32.dll | SysWOW64 | detoured.dll |
| atiapfxx.blb | SysWOW64 | atiapfxx.blb |
| ativvsva.dat | SysWOW64 | ativvsva.dat |
| ativvsvl.dat | SysWOW64 | ativvsvl.dat |
| amdvlk32.dll | SysWOW64 | vulkan-1.dll |

---

## 4. Qualcomm (QCOM)

### System32 (Native ARM64)
| Host Source File | Guest Target Directory | Guest Target Filename |
| :--- | :--- | :--- |
| OpenCL.dll | System32 | OpenCL.dll |
| qcdxkmsuc8380.mbn | System32 | qcdxkmsuc8380.mbn |
| qchdcpumd8380.dll | System32 | qchdcpumd8380.dll |
| qcdx8380.cat | System32\CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE} | oem7.cat |

### SysWOW64 (x86 Compatibility)
| Host Source File | Guest Target Directory | Guest Target Filename |
| :--- | :--- | :--- |
| qcdx11x86um.dll | SysWOW64 | qcdx11x86um.dll |
| qcdx12x86um.dll | SysWOW64 | qcdx12x86um.dll |
| qcdxdmlx86.dll | SysWOW64 | qcdxdmlx86.dll |
| qcdxsdx86.dll | SysWOW64 | qcdxsdx86.dll |
| qcegpx86.dll | SysWOW64 | qcegpx86.dll |
| qcgpux86compilercore.DLL | SysWOW64 | qcgpux86compilercore.DLL |
| qcvidencx86um.DLL | SysWOW64 | qcvidencum.DLL |

### SyChpe32 (CHPE Emulation)
| Host Source File | Guest Target Directory | Guest Target Filename |
| :--- | :--- | :--- |
| qcdx11chpeum.dll | SyChpe32 | qcdx11x86um.dll |
| qcdx12chpeum.dll | SyChpe32 | qcdx12x86um.dll |
| qcdxdmlchpe.dll | SyChpe32 | qcdxdmlx86.dll |
| qcdxsdchpe.dll | SyChpe32 | qcdxsdx86.dll |
| qcegpchpe.dll | SyChpe32 | qcegpdx86.dll |
| qcgpuchpecompilercore.dll | SyChpe32 | qcgpux86compilercore.DLL |