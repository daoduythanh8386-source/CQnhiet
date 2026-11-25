"""Kiểm tra bản đồ ô so với template thật: ô ghi phải là ô trên-trái vùng gộp (hoặc không gộp), nằm trong vùng in, nhãn khớp."""
import json,sys,glob,openpyxl,re,warnings
from openpyxl.utils import range_boundaries
warnings.filterwarnings('ignore')
root=sys.argv[1]; fails=0; checks=0
for mf in sorted(glob.glob(root+'/config/template-maps/*.json')):
    m=json.load(open(mf)); wb=openpyxl.load_workbook(root+'/sources/templates/'+m['fileName'])
    ws=wb[m['sheet']]; pa=range_boundaries(ws.print_area.split('!')[1].replace('$',''))
    merges=[range_boundaries(str(r)) for r in ws.merged_cells.ranges]
    def topleft_ok(cell):
        c=ws[cell]; col,row=c.column,c.row
        for (c1,r1,c2,r2) in merges:
            if c1<=col<=c2 and r1<=row<=r2: return (col,row)==(c1,r1)
        return True
    def inprint(cell):
        c=ws[cell]; return pa[0]<=c.column<=pa[2] and pa[1]<=c.row<=pa[3]
    for name,f in m['fields'].items():
        cells=[f['cell']] if 'cell' in f else f.get('cells',[])
        rngs=[f['range']] if 'range' in f else f.get('ranges',[])
        for c in cells:
            checks+=1
            if not topleft_ok(c): print(f"FAIL {m['templateId']}.{name}: {c} không phải ô trên-trái vùng gộp"); fails+=1
            if not inprint(c): print(f"FAIL {m['templateId']}.{name}: {c} ngoài vùng in"); fails+=1
        for r in rngs:
            checks+=1
            b=range_boundaries(r)
            if b not in merges: print(f"WARN {m['templateId']}.{name}: khung {r} không trùng một vùng gộp")
            if not (pa[0]<=b[0] and b[2]<=pa[2] and pa[1]<=b[1] and b[3]<=pa[3]): print(f"FAIL {m['templateId']}.{name}: khung {r} ngoài vùng in"); fails+=1
        if 'label' in f:
            checks+=1
            lc,lt=f['label']; v=ws[lc].value
            if not (v is not None and str(lt).lower() in str(v).lower()): print(f"FAIL {m['templateId']}.{name}: nhãn {lc}={v!r} không chứa {lt!r}"); fails+=1
print(f"{checks} kiểm tra, {fails} lỗi"); sys.exit(1 if fails else 0)
