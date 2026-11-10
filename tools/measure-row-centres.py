import sys
from PIL import Image
# usage: clusters.py image y0 y1 [x0 x1] [gap] ; prints ink clusters in the band with vertical extent and centre
path=sys.argv[1]; y0=int(sys.argv[2]); y1=int(sys.argv[3])
im=Image.open(path).convert('RGB'); px=im.load(); W,H=im.size
x0=int(sys.argv[4]) if len(sys.argv)>4 else 0; x1=int(sys.argv[5]) if len(sys.argv)>5 else W
gap=int(sys.argv[6]) if len(sys.argv)>6 else 7
# background per column = mode-ish: use the pixel at band edges
def lum(p): return sum(p)/3
bgl=sorted(lum(px[x,y0]) for x in range(x0,x1))[len(range(x0,x1))//2]
def ink(x,y): return abs(lum(px[x,y])-bgl)>45
cols=[x for x in range(x0,x1) if any(ink(x,y) for y in range(y0,y1))]
runs=[]
for x in cols:
    if runs and x-runs[-1][1]<=gap: runs[-1][1]=x
    else: runs.append([x,x])
centre=(y0+y1)/2
for a,b in runs:
    ys=[y for y in range(y0,y1) if any(ink(x,y) for x in range(a,b+1))]
    print(f'  x {a:4d}-{b:4d}  rows {min(ys):4d}-{max(ys):4d}  h {max(ys)-min(ys)+1:3d}  centre {(min(ys)+max(ys))/2:7.1f}  vs band {centre:6.1f}: {((min(ys)+max(ys))/2-centre):+5.1f}')
