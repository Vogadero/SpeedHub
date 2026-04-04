from PIL import Image, ImageDraw, ImageFont
import math

# 创建透明背景图像
size = 512
img = Image.new('RGBA', (size, size), (0, 0, 0, 0))  # 完全透明
draw = ImageDraw.Draw(img)

# 绘制 "S" 字母
center_x, center_y = size // 2, size // 2
s_width = 280
s_height = 320
start_x = center_x - s_width // 2
start_y = center_y - s_height // 2

# 创建渐变
def create_gradient(width, height, color1, color2):
    gradient = Image.new('RGBA', (width, height), (0, 0, 0, 0))
    for y in range(height):
        r = int(color1[0] + (color2[0] - color1[0]) * y / height)
        g = int(color1[1] + (color2[1] - color1[1]) * y / height)
        b = int(color1[2] + (color2[2] - color1[2]) * y / height)
        for x in range(width):
            gradient.putpixel((x, y), (r, g, b, 255))
    return gradient

# 绘制 S 的路径（使用多边形近似）
# S 的上半部分（圆弧）
s_points = []

# 顶部横线
for x in range(0, s_width, 2):
    s_points.append((start_x + x, start_y + 40))

# 右侧向下
for y in range(40, s_height // 2, 2):
    s_points.append((start_x + s_width, start_y + y))

# 中间横线
for x in range(s_width, 0, -2):
    s_points.append((start_x + x, start_y + s_height // 2))

# 左侧向下
for y in range(s_height // 2, s_height - 40, 2):
    s_points.append((start_x, start_y + y))

# 底部横线
for x in range(0, s_width, 2):
    s_points.append((start_x + x, start_y + s_height - 40))

# 绘制渐变 S
gradient = create_gradient(s_width, s_height, (0, 212, 255), (0, 144, 179))

# 创建 S 的蒙版
mask = Image.new('L', (size, size), 0)
mask_draw = ImageDraw.Draw(mask)

# 绘制 S 形状（使用粗线条）
line_width = 70

# 上横线
mask_draw.rounded_rectangle(
    [start_x, start_y + 20, start_x + s_width, start_y + 60],
    radius=20,
    fill=255
)

# 右竖线
mask_draw.rounded_rectangle(
    [start_x + s_width - line_width, start_y + 40, start_x + s_width, start_y + s_height // 2 + line_width // 2],
    radius=20,
    fill=255
)

# 中横线
mask_draw.rounded_rectangle(
    [start_x, start_y + s_height // 2 - 30, start_x + s_width, start_y + s_height // 2 + 30],
    radius=20,
    fill=255
)

# 左竖线
mask_draw.rounded_rectangle(
    [start_x, start_y + s_height // 2 - line_width // 2, start_x + line_width, start_y + s_height - 40],
    radius=20,
    fill=255
)

# 下横线
mask_draw.rounded_rectangle(
    [start_x, start_y + s_height - 60, start_x + s_width, start_y + s_height - 20],
    radius=20,
    fill=255
)

# 应用渐变到 S
gradient_pasted = Image.new('RGBA', (size, size), (0, 0, 0, 0))
gradient_pasted.paste(gradient, (start_x, start_y))

# 使用蒙版
result = Image.composite(gradient_pasted, img, mask)

# 保存为多尺寸 ICO
output_ico = r"D:\SpeedHub-repo\assets\app-icon.ico"
sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]

# 调整大小
images = [result.resize(size, Image.LANCZOS) for size in sizes]

# 保存为 ICO
images[0].save(
    output_ico,
    format='ICO',
    sizes=[img.size for img in images],
    append_images=images[1:]
)

# 同时保存 PNG 预览
output_png = r"D:\SpeedHub-repo\assets\app-icon.png"
result.save(output_png, 'PNG')

print(f"Icon generated with transparent background: {output_ico}")
print(f"Mode: {result.mode}")
print(f"Sizes: {', '.join([f'{s[0]}x{s[1]}' for s in sizes])}")
