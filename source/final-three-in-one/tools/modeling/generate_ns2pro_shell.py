"""Original mesh traced against user-supplied NS2 Pro front/back reference views.
Offline authoring: numpy/scipy/scikit-image/matplotlib. No third-party mesh.
148 mm width sets scene scale; hidden surface depth is an artistic reconstruction.
"""
from pathlib import Path
import struct
import numpy as np
from scipy.interpolate import CubicSpline
from scipy.spatial import cKDTree
from scipy.ndimage import map_coordinates
from matplotlib.path import Path as Polygon
from skimage.measure import marching_cubes

# Right half, clockwise from crown to opening: long inward-sloping grip ends.
half=np.array([(0,48),(28,47.6),(48,45.4),(57,40.5),(61,32),
 (65,18),(69,-2),(72,-22),(73.7,-33),(73,-41),(70,-47),
 (65,-51.5),(59,-53),(53,-51.5),(47.5,-47),(43,-39),
 (39,-30),(35,-26.7),(28,-26),(0,-26)],dtype=float)
outline=np.concatenate((half,np.column_stack((-half[-2:0:-1,0],half[-2:0:-1,1])),half[:1]))
arc=np.concatenate(([0],np.cumsum(np.linalg.norm(np.diff(outline,axis=0),axis=1))))
spline=CubicSpline(arc,outline,bc_type='periodic')
curve=spline(np.linspace(0,arc[-1],2400,endpoint=False))
curve[:,0]*=148/np.ptp(curve[:,0])
step=1.0
xs=np.arange(-79,80,step,dtype=np.float32)
ys=np.arange(-59,54,step,dtype=np.float32)
zs=np.arange(-34,30,step,dtype=np.float32)
xx,yy=np.meshgrid(xs,ys,indexing='ij')
samples=np.stack((xx,yy),axis=-1)
distance=cKDTree(curve).query(samples.reshape(-1,2))[0].reshape(xx.shape)
distance*=np.where(Polygon(curve).contains_points(samples.reshape(-1,2)).reshape(xx.shape),-1,1)
# Rolled edges through thickness, keeping the entire shell a single surface.
x,y,z=np.meshgrid(xs,ys,zs,indexing='ij')
edge=distance[:,:,None]+9
front=22.0-.35*(x/74)**2
back=-25+2.2*(x/74)**2+1.0*(y/53)**2
mid=(front+back)/2
depth=np.abs(z-mid)-(front-back)/2+9
field=(np.sqrt(np.maximum(edge,0)**2+np.maximum(depth,0)**2)
       +np.minimum(np.maximum(edge,depth),0)-9).astype(np.float32)
vertices,faces,_,_=marching_cubes(field,0,spacing=(step,step,step),
 gradient_direction='ascent',allow_degenerate=False)
origin=np.array((xs[0],ys[0],zs[0]));vertices+=origin
indices=(vertices-origin)/step
normals=np.stack([map_coordinates(g,indices.T,order=1,mode='nearest')
 for g in np.gradient(field,step)],axis=-1)
normals/=np.maximum(np.linalg.norm(normals,axis=-1,keepdims=True),1e-9)
tri=vertices[faces]
if np.sum(np.cross(tri[:,1]-tri[:,0],tri[:,2]-tri[:,0])*normals[faces].mean(axis=1))<0:
 faces=faces[:,[0,2,1]]
centers=vertices[faces].mean(axis=1)
material=np.full(len(faces),1,dtype=np.uint8)
material[centers[:,2]<-1]=2
material[(centers[:,2]>18)&(centers[:,1]>-22)&(np.abs(centers[:,0])<62)]=0
uv=np.stack(((vertices[:,0]+80)/160,(vertices[:,1]+60)/120),axis=1)
packed=np.concatenate((vertices,normals,uv),axis=1).astype('<f4')
out=Path(__file__).resolve().parents[2]/'windows/v60_viiper_app/Assets/Controller/ns2pro-shell.mesh'
with out.open('wb') as f:
 f.write(b'N2M1');f.write(struct.pack('<II',len(vertices),len(faces)))
 f.write(packed.tobytes());f.write(faces.astype('<u4').tobytes());f.write(material.tobytes())
print(f'{len(vertices)} vertices, {len(faces)} triangles; bounds {vertices.min(0)} .. {vertices.max(0)}; {out}')
