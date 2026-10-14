# Vẽ icon app (cờ lê + bánh răng). Chạy: python tools/make_icon.py src/ITHelpdeskTool/Assets preview.png
import sys, math
from PIL import Image, ImageDraw, ImageChops

S = 1024
C = 512

def lerp(a, b, t): return tuple(int(a[i] + (b[i]-a[i])*t) for i in range(3))

def background():
    c1, c2 = (0x4F, 0x8C, 0xFF), (0x8B, 0x5C, 0xF6)
    grad = Image.new("RGB", (S, S)); px = grad.load()
    for y in range(S):
        for x in range(S):
            px[x, y] = lerp(c1, c2, (x + y) / (2*S - 2))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((24, 24, S-24, S-24), radius=230, fill=255)
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0)); img.paste(grad, (0, 0), mask)
    return img

def rot(p, ang, o=(C, C)):
    x, y = p[0]-o[0], p[1]-o[1]; c, s = math.cos(ang), math.sin(ang)
    return (o[0] + x*c - y*s, o[1] + x*s + y*c)

def gear_mask(cx, cy, r, hole, teeth=8, tooth_w_in=120, tooth_w_out=88, tooth_h=78):
    m = Image.new("L", (S, S), 0); d = ImageDraw.Draw(m)
    d.ellipse((cx-r, cy-r, cx+r, cy+r), fill=255)
    for k in range(teeth):
        a = 2*math.pi*k/teeth
        base = r - 20
        pts = [(-tooth_w_in/2, -base), (tooth_w_in/2, -base),
               (tooth_w_out/2, -(r+tooth_h)), (-tooth_w_out/2, -(r+tooth_h))]
        pts = [rot((cx+x, cy+y), a, (cx, cy)) for x, y in pts]
        d.polygon(pts, fill=255)
    d.ellipse((cx-hole, cy-hole, cx+hole, cy+hole), fill=0)
    return m

def wrench_mask(pad=0):
    """Cờ lê nằm chéo: đầu mở ở góc trên-phải, cán xuống góc dưới-trái."""
    m = Image.new("L", (S, S), 0); d = ImageDraw.Draw(m)
    ang = math.radians(-45)               # hướng trục từ dưới-trái lên trên-phải
    ux, uy = math.cos(ang), math.sin(ang) # vector dọc trục
    vx, vy = -uy, ux                      # vector ngang trục
    head = (C + 205, C - 205)             # tâm đầu cờ lê
    tail = (C - 225, C + 225)             # tâm đuôi cán
    hw = 50 + pad                         # nửa bề rộng cán
    # cán
    pts = [(tail[0]+vx*hw, tail[1]+vy*hw), (head[0]+vx*hw, head[1]+vy*hw),
           (head[0]-vx*hw, head[1]-vy*hw), (tail[0]-vx*hw, tail[1]-vy*hw)]
    d.polygon(pts, fill=255)
    tr = hw
    d.ellipse((tail[0]-tr, tail[1]-tr, tail[0]+tr, tail[1]+tr), fill=255)
    # đầu tròn
    hr = 138 + pad
    d.ellipse((head[0]-hr, head[1]-hr, head[0]+hr, head[1]+hr), fill=255)
    # ngàm mở: rãnh hướng ra ngoài theo trục (chỉ cắt khi không phải mask đệm)
    sw = 50 - pad                         # nửa bề rộng rãnh
    if sw > 0 and pad == 0:
        depth0 = 10 + pad
        far = 400
        p0 = (head[0]+ux*depth0, head[1]+uy*depth0)
        p1 = (head[0]+ux*far, head[1]+uy*far)
        slot = [(p0[0]+vx*sw, p0[1]+vy*sw), (p1[0]+vx*sw, p1[1]+vy*sw),
                (p1[0]-vx*sw, p1[1]-vy*sw), (p0[0]-vx*sw, p0[1]-vy*sw)]
        d.polygon(slot, fill=0)
        d.ellipse((p0[0]-sw, p0[1]-sw, p0[0]+sw, p0[1]+sw), fill=0)
    return m

def render():
    img = background()
    gear = gear_mask(C, C, 230, 138)
    gap = wrench_mask(pad=26)
    gear = ImageChops.subtract(gear, gap)          # chừa khe giữa bánh răng và cờ lê
    wrench = wrench_mask()
    shape = ImageChops.lighter(gear, wrench)
    # bóng đổ nhẹ cho khối trắng
    from PIL import ImageFilter
    sh = shape.filter(ImageFilter.GaussianBlur(18))
    shadow = Image.new("RGBA", (S, S), (40, 20, 90, 0)); shadow.putalpha(sh.point(lambda v: v*55//255))
    img.alpha_composite(shadow, (0, 14))
    white = Image.new("RGBA", (S, S), (255, 255, 255, 0)); white.putalpha(shape)
    # giữ trong khung bo góc
    frame = Image.new("L", (S, S), 0)
    ImageDraw.Draw(frame).rounded_rectangle((24, 24, S-24, S-24), radius=230, fill=255)
    a = ImageChops.multiply(img.getchannel("A"), frame); img.putalpha(a)
    img.alpha_composite(white)
    return img

big = render()
out = sys.argv[1]
big.resize((256, 256), Image.LANCZOS).save(out + "/app.png")
big.save(out + "/app.ico", sizes=[(s, s) for s in [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]])
canvas = Image.new("RGBA", (900, 390), (14, 16, 20, 255)); x = 20
for s in [256, 128, 64, 48, 32, 24, 16]:
    canvas.alpha_composite(big.resize((s, s), Image.LANCZOS), (x, 30 + (256-s)//2)); x += s + 22
lite = Image.new("RGBA", (900, 60), (245, 246, 248, 255)); x = 20
for s in [48, 32, 24, 16]:
    lite.alpha_composite(big.resize((s, s), Image.LANCZOS), (x, 6 + (48-s)//2)); x += s + 20
canvas.alpha_composite(lite, (0, 330))
canvas.save(sys.argv[2])
