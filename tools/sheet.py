import sys
from PIL import Image, ImageDraw
out=sys.argv[1]; files=sys.argv[2:]
ims=[Image.open(f).convert('RGB') for f in files]
w=max(i.width for i in ims); h=max(i.height for i in ims); cols=min(4,len(ims)); rows=(len(ims)+cols-1)//cols
S=Image.new('RGB',(cols*w,rows*(h+18)),(20,20,20)); d=ImageDraw.Draw(S)
for k,(f,i) in enumerate(zip(files,ims)):
    x=(k%cols)*w; y=(k//cols)*(h+18); S.paste(i,(x,y+18)); d.text((x+4,y+3),f.split('/')[-1],fill=(230,230,200))
S.save(out)
