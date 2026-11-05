"""Render the app's geometric icon; Pillow is needed only to regenerate assets."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parent
scale = 4
im = Image.new('RGBA', (256 * scale, 256 * scale))
d = ImageDraw.Draw(im)
def box(coords, radius, fill):
    d.rounded_rectangle(tuple(int(v * scale) for v in coords), radius=radius * scale, fill=fill)
def polygon(coords, fill):
    d.polygon([(int(x * scale), int(y * scale)) for x, y in coords], fill=fill)

# A simple silhouette remains readable at 16 px in the title bar and tray.
box((8, 8, 248, 248), 56, '#285BE2')
box((53, 51, 203, 217), 20, '#1B45B8')
box((49, 40, 199, 207), 18, '#FFFFFF')
polygon([(151, 40), (199, 40), (199, 88)], '#285BE2')
polygon([(151, 40), (151, 76), (154, 84), (162, 88), (199, 88)], '#FFD66B')
box((76, 105, 172, 116), 5, '#285BE2')
box((76, 132, 172, 143), 5, '#285BE2')
box((76, 159, 139, 170), 5, '#285BE2')
im = im.resize((256, 256), Image.Resampling.LANCZOS)
im.save(root / 'app.png')
im.save(root / 'app.ico', sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])
