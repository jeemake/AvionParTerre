"""Rend l'icône « Avion par terre » (tools/icon-anim/avion-icone-anim.html) en PNG pour le plugin.

Le moteur de rendu JavaScript de la page est exécuté tel quel avec Node, puis les images sont
enregistrées avec Pillow :
  - src/AvionParTerre.Revit/Resources/avion_32.png, avion_16.png  (image 0, ton vert : icône du ruban)
  - src/AvionParTerre.Revit/Resources/avion_sprite.png             (60 images 64 px : animation d'attente)
Usage : python -X utf8 tools/render_icon.py
"""
import json, pathlib, re, subprocess
from PIL import Image

root = pathlib.Path(__file__).resolve().parent
html = (root / "icon-anim" / "avion-icone-anim.html").read_text(encoding="utf-8")
script = re.search(r'"use strict";(.*?)// ─+ principes', html, re.S).group(1)
driver = script + r"""
const out = {};
for (const tone of ['vert', 'blanc']) {
  out[tone] = [];
  for (let f = 0; f < 60; f++) {
    const img = { data: new Uint8ClampedArray(N * N * 4) };
    render(f / 12, tone, img);
    out[tone].push(Array.from(img.data));
  }
}
process.stdout.write(JSON.stringify(out));
"""
res = subprocess.run(["node", "-e", driver], capture_output=True, text=True, encoding="utf-8", check=True)
frames = json.loads(res.stdout)
N = 32

def frame(tone, i):
    return Image.frombytes("RGBA", (N, N), bytes(frames[tone][i]))

dest = root.parent / "src" / "AvionParTerre.Revit" / "Resources"
dest.mkdir(parents=True, exist_ok=True)
f0 = frame("vert", 0)
f0.save(dest / "avion_32.png")
f0.resize((16, 16), Image.LANCZOS).save(dest / "avion_16.png")
sprite = Image.new("RGBA", (60 * 64, 64))
for i in range(60):
    sprite.paste(frame("vert", i).resize((64, 64), Image.NEAREST), (i * 64, 0))
sprite.save(dest / "avion_sprite.png")
print("Icônes écrites dans", dest)
