"""Icône de l'exe (src\\GenerateFollower.ico) : bouclier bleu avec « GF », même dessin que l'icône du HIMBO Converter.
Usage (depuis le dossier GenerateFollower) : py beta\\make_icon_gf.py   (Pillow)"""
from PIL import Image, ImageDraw, ImageFont
S = 256
im = Image.new('RGBA', (S, S), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
d.polygon([(28, 24), (228, 24), (228, 120), (200, 180), (128, 236), (56, 180), (28, 120)],
          fill=(38, 62, 102), outline=(200, 205, 215), width=10)
d.polygon([(52, 46), (204, 46), (204, 116), (182, 164), (128, 208), (74, 164), (52, 116)], fill=(52, 86, 138))
try: f = ImageFont.truetype('arialbd.ttf', 104)
except OSError: f = ImageFont.load_default()
d.text((128, 118), 'GF', font=f, fill=(236, 240, 248), anchor='mm')
im.save(r'src\GenerateFollower.ico', sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
