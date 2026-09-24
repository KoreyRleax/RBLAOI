# -*- coding: utf-8 -*-
import xml.etree.ElementTree as ET
t = ET.parse(r"C:\Users\Administrator\Desktop\RBLAOI\bin\x64\Debug\Board\215\ProjectConfig.xml")
r = t.getroot()
print("根节点子项:")
for child in r:
    print(" ", child.tag, len(child) if len(child) else child.text)
found = False
for dp in r.iter():
    if 'Detect' in dp.tag and len(dp) > 0:
        print("\n节点:", dp.tag, "子项:", len(dp))
        for i, c in enumerate(dp):
            x = c.find('X'); y = c.find('Y')
            if x is not None and y is not None:
                print(f"  检测位{i:2d}: X={float(x.text):8.3f}  Y={float(y.text):8.3f}")
        found = True
        break
if not found:
    print("未找到 DetectPositions，打印全文标签:")
    for el in r.iter():
        print(el.tag)
        if el.tag and len(list(el)) == 0 and el.text and el.text.strip():
            pass
