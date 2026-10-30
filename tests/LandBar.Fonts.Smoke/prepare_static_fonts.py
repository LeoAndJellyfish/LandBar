from pathlib import Path
import argparse
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

root=Path(__file__).resolve().parents[2]
target=root/'src/LandBar/Assets/Fonts'
parser=argparse.ArgumentParser(description='Create WPF-compatible Regular and SemiBold static instances.')
parser.add_argument('--source',type=Path,default=Path('C:/Windows/Fonts/NotoSansSC-VF.ttf'))
source=parser.parse_args().source
for weight, style in [(400,'Regular'),(600,'SemiBold')]:
    destination=target/f'NotoSansSC-{style}.ttf'
    font=TTFont(source,recalcTimestamp=False)
    font=instantiateVariableFont(font, {'wght':weight}, inplace=True, optimize=True, updateFontNames=False)
    names=font['name']
    # WPF groups resource fonts by typographic family and reads weight from OS/2.
    replacements={1:'Noto Sans SC',2:style,3:f'NotoSansSC-{style};LandBarStatic;{weight}',4:f'Noto Sans SC {style}',6:f'NotoSansSC-{style}',16:'Noto Sans SC',17:style}
    for record in list(names.names):
        if record.nameID in replacements:
            names.setName(replacements[record.nameID],record.nameID,record.platformID,record.platEncID,record.langID)
    for name_id, value in replacements.items():
        names.setName(value,name_id,3,1,0x409)
    font['OS/2'].usWeightClass=weight
    font['OS/2'].fsSelection &= ~(1 << 5)
    if weight==400: font['OS/2'].fsSelection |= 1 << 6
    else: font['OS/2'].fsSelection &= ~(1 << 6)
    font['head'].macStyle &= ~1
    # A pinned instance has no variation axes. A residual STAT fallback name can make
    # WPF classify the 600 face as Regular / 700 even though OS/2 already says 600.
    if 'STAT' in font: del font['STAT']
    font.save(destination)
    print(f'{destination.name}: {destination.stat().st_size}',flush=True)
