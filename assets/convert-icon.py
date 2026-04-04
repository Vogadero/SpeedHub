from PIL import Image
import os

# 输入和输出路径
input_png = r"D:\SpeedHub-repo\assets\app-icon.png"
output_ico = r"D:\SpeedHub-repo\assets\app-icon.ico"

# 打开 PNG 图像
img = Image.open(input_png)

# 定义 .ico 需要的多个尺寸
sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]

# 调整大小并保存为 .ico
images = [img.resize(size, Image.LANCZOS) for size in sizes]

# 保存为 ICO 格式
images[0].save(
    output_ico,
    format='ICO',
    sizes=[img.size for img in images],
    append_images=images[1:]
)

print(f"Icon generated: {output_ico}")
print(f"Sizes: {', '.join([f'{s[0]}x{s[1]}' for s in sizes])}")
