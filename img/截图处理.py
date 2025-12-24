import tkinter as tk
from tkinter import filedialog, messagebox
import os
import subprocess
import sys

def process_images():
    # 1. 初始化 Tkinter，隐藏主窗口
    root = tk.Tk()
    root.withdraw()

    # 2. 弹出文件夹选择框
    print("正在等待用户选择文件夹...")
    folder_path = filedialog.askdirectory(title="请选择包含图片的文件夹")

    if not folder_path:
        print("未选择文件夹，程序退出。")
        return

    # 3. 定义输出文件夹 (在原文件夹下新建 "cropped_output")
    output_dir = os.path.join(folder_path, "cropped_output")
    if not os.path.exists(output_dir):
        os.makedirs(output_dir)

    # 支持的图片格式
    valid_extensions = ('.jpg', '.jpeg', '.png', '.bmp', '.tif', '.tiff', '.webp')
    
    # 统计计数
    success_count = 0
    file_list = os.listdir(folder_path)
    total_files = len([f for f in file_list if f.lower().endswith(valid_extensions)])

    if total_files == 0:
        messagebox.showwarning("提示", "所选文件夹中没有找到支持的图片文件。")
        return

    print(f"找到 {total_files} 张图片，开始处理...")

    # 4. 遍历文件夹并调用 FFmpeg
    for filename in file_list:
        if filename.lower().endswith(valid_extensions):
            input_path = os.path.join(folder_path, filename)
            output_path = os.path.join(output_dir, filename)

            # 构建 FFmpeg 命令
            # -i: 输入文件
            # -vf: 视频/图片过滤器
            # crop=iw-2:ih:2:0 解析:
            #   iw-2: 输出宽度 = 输入宽度 - 2
            #   ih:   输出高度 = 输入高度 (不变)
            #   2:    X轴起始点 = 2 (即切掉左边 0, 1 两个像素)
            #   0:    Y轴起始点 = 0
            # -y: 覆盖输出文件不询问
            
            cmd = [
                'ffmpeg', 
                '-hide_banner', '-loglevel', 'error', # 减少控制台输出
                '-y', 
                '-i', input_path, 
                '-vf', 'crop=iw-2:ih:2:0', 
                output_path
            ]

            try:
                # 执行命令，不弹出新的CMD窗口
                # Windows下使用 startupinfo 隐藏控制台闪烁 (可选)
                startupinfo = None
                if os.name == 'nt':
                    startupinfo = subprocess.STARTUPINFO()
                    startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW

                subprocess.run(cmd, check=True, startupinfo=startupinfo)
                print(f"[成功] {filename}")
                success_count += 1
            except subprocess.CalledProcessError as e:
                print(f"[失败] {filename} - FFmpeg 出错")
            except FileNotFoundError:
                messagebox.showerror("错误", "未找到 ffmpeg 命令，请确保已将其添加到系统环境变量 PATH 中。")
                return

    # 5. 完成提示
    print(f"处理完成。成功: {success_count}/{total_files}")
    messagebox.showinfo("完成", f"处理完成！\n\n成功处理: {success_count} 张\n文件已保存至: {output_dir}")

if __name__ == "__main__":
    process_images()