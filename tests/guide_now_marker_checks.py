"""Check actual painted Now-line pixels without installing image dependencies."""
import base64
import json


def check_now_marker(page, root, args):
    page.wait_for_timeout(150)
    geometry=root.evaluate('''el=>{
      const grid=el.querySelector('.programGrid').getBoundingClientRect(),column=el.querySelector('.channelsContainer').getBoundingClientRect(),
        pct=parseFloat(getComputedStyle(el).getPropertyValue('--fin-guide-now'))/100;
      const rows=Array.from(el.querySelectorAll('.channelPrograms')).filter(row=>{const r=row.getBoundingClientRect();return r.top>=grid.top&&r.top+10<innerHeight;});
      return {columnRight:column.right,gridLeft:grid.left,gridRight:grid.right,
        samples:rows.map(row=>{const r=row.getBoundingClientRect();return {channel:row.dataset.channelid,x:r.left+r.width*pct+1,y:r.top+8,height:Math.floor(r.height-16),hasProgramme:!!row.querySelector('.programCell')};})};
    }''')
    assert geometry['samples']
    image=page.screenshot(path=str(args.output/'now-line.png'))
    samples=page.evaluate('''async ({image,samples})=>{
      const img=new Image();img.src='data:image/png;base64,'+image;await img.decode();
      const canvas=document.createElement('canvas');canvas.width=img.naturalWidth;canvas.height=img.naturalHeight;
      const context=canvas.getContext('2d');context.drawImage(img,0,0);
      return samples.map(sample=>({...sample,pixels:[-1,0,1].map(offset=>Array.from(context.getImageData(Math.floor(sample.x)+offset,Math.floor(sample.y),1,1).data)),
        linePixels:Array.from({length:sample.height},(_,y)=>[-1,0,1].map(offset=>Array.from(context.getImageData(Math.floor(sample.x)+offset,Math.floor(sample.y)+y,1,1).data)))}));
    }''', {'image':base64.b64encode(image).decode(),'samples':geometry['samples']})
    expected=(198,233,239)
    for sample in samples:
        sample['visible']=any(max(abs(pixel[i]-expected[i]) for i in range(3))<20 for pixel in sample['pixels'])
        sample['visiblePixelFraction']=sum(any(max(abs(pixel[i]-expected[i]) for i in range(3))<20 for pixel in pixels) for pixels in sample.pop('linePixels'))/sample['height']
        assert geometry['columnRight']+4<sample['x']<geometry['gridRight'],geometry
    receipt={**geometry,'samples':samples,'visibleOverProgrammeBodies':all(s['visible'] and s['visiblePixelFraction']>=.25 for s in samples if s['hasProgramme'])}
    (args.output/'now-line.json').write_text(json.dumps(receipt,indent=2))
    assert any(s['hasProgramme'] for s in samples)
    assert receipt['visibleOverProgrammeBodies'],receipt
