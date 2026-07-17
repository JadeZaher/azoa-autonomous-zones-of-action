// <hero-dag> — WebGL quest-DAG simulation: agents traverse a living graph.
(function(){
if (customElements.get('hero-dag')) return;
function hex(c, fb){ if(!c) return fb; c=c.replace('#',''); if(c.length===3) c=c.split('').map(x=>x+x).join('');
  return [parseInt(c.slice(0,2),16)/255, parseInt(c.slice(2,4),16)/255, parseInt(c.slice(4,6),16)/255]; }
const VS=`attribute vec3 aPos;attribute vec4 aCol;attribute float aSize;
uniform float uRotY,uRotX,uAspect,uDpr;varying vec4 vCol;
void main(){vec3 p=aPos;float cy=cos(uRotY),sy=sin(uRotY);
p=vec3(cy*p.x+sy*p.z,p.y,-sy*p.x+cy*p.z);
float cx=cos(uRotX),sx=sin(uRotX);
p=vec3(p.x,cx*p.y-sx*p.z,sx*p.y+cx*p.z);
float d=3.4;float w=d/(d-p.z);
gl_Position=vec4(p.x*w/uAspect,p.y*w,p.z*0.1,1.0);
gl_PointSize=aSize*w*uDpr;vCol=aCol;}`;
const FS=`precision mediump float;varying vec4 vCol;uniform float uPoint;
void main(){float a=vCol.a;
if(uPoint>0.5){vec2 c=gl_PointCoord-0.5;float r=length(c);if(r>0.5)discard;a*=smoothstep(0.5,0.32,r);}
gl_FragColor=vec4(vCol.rgb,a);}`;
class HeroDag extends HTMLElement {
  connectedCallback(){
    this.style.display='block';
    const cv=document.createElement('canvas');
    cv.style.cssText='width:100%;height:100%;display:block;';
    this.appendChild(cv); this._cv=cv;
    const gl=cv.getContext('webgl',{alpha:true,antialias:true,premultipliedAlpha:false});
    if(!gl) return; this._gl=gl;
    const mk=(t,s)=>{const sh=gl.createShader(t);gl.shaderSource(sh,s);gl.compileShader(sh);return sh;};
    const pr=gl.createProgram();
    gl.attachShader(pr,mk(gl.VERTEX_SHADER,VS)); gl.attachShader(pr,mk(gl.FRAGMENT_SHADER,FS));
    gl.linkProgram(pr); gl.useProgram(pr); this._pr=pr;
    this._loc={pos:gl.getAttribLocation(pr,'aPos'),col:gl.getAttribLocation(pr,'aCol'),size:gl.getAttribLocation(pr,'aSize'),
      rotY:gl.getUniformLocation(pr,'uRotY'),rotX:gl.getUniformLocation(pr,'uRotX'),aspect:gl.getUniformLocation(pr,'uAspect'),
      dpr:gl.getUniformLocation(pr,'uDpr'),point:gl.getUniformLocation(pr,'uPoint')};
    this._buf=gl.createBuffer();
    gl.enable(gl.BLEND); gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);
    this._build();
    this._ro=new ResizeObserver(()=>this._resize()); this._ro.observe(this); this._resize();
    this._t=0; this._last=performance.now(); this._rewireT=0;
    const loop=(now)=>{this._raf=requestAnimationFrame(loop);
      const dt=Math.min(0.05,(now-this._last)/1000); this._last=now;
      this._step(dt); this._draw();};
    this._raf=requestAnimationFrame(loop);
  }
  disconnectedCallback(){ cancelAnimationFrame(this._raf); if(this._ro)this._ro.disconnect(); }
  _resize(){ const d=Math.min(2,devicePixelRatio||1); const w=this.clientWidth,h=this.clientHeight;
    this._cv.width=Math.max(1,w*d); this._cv.height=Math.max(1,h*d); this._dpr=d;
    this._gl.viewport(0,0,this._cv.width,this._cv.height); this._aspect=w/Math.max(1,h); }
  _build(){
    const density=parseFloat(this.getAttribute('density')||'1');
    const L=6, nodes=[], layers=[];
    for(let i=0;i<L;i++){
      const n=Math.round((4+Math.floor(Math.random()*4))*density), layer=[];
      for(let j=0;j<n;j++){
        const nd={x:-1.5+i*(3.0/(L-1)), y:(Math.random()*2-1)*0.95, z:(Math.random()*2-1)*0.9,
          ny:0,nz:0,lerp:1, layer:i, state:0, st:Math.random()*6, ph:Math.random()*6.28, f:0.3+Math.random()*0.5, born:1};
        layer.push(nodes.length); nodes.push(nd);
      }
      layers.push(layer);
    }
    const edges=[];
    for(let i=0;i<L-1;i++) for(const a of layers[i]){
      const k=1+Math.floor(Math.random()*2.4);
      const targets=[...layers[i+1]].sort(()=>Math.random()-0.5).slice(0,k);
      for(const b of targets) edges.push([a,b]);
    }
    const agents=[];
    for(let i=0;i<11;i++) agents.push({e:Math.floor(Math.random()*edges.length), p:Math.random(), v:0.25+Math.random()*0.4});
    this._nodes=nodes; this._layers=layers; this._edges=edges; this._agents=agents;
  }
  _step(dt){
    const speed=parseFloat(this.getAttribute('speed')||'1'); dt*=speed;
    this._t+=dt; this._rewireT+=dt;
    const N=this._nodes, E=this._edges;
    for(const nd of N){
      nd.st+=dt;
      if(nd.lerp<1){ nd.lerp=Math.min(1,nd.lerp+dt*1.2); const k=nd.lerp*nd.lerp*(3-2*nd.lerp);
        nd.y=nd.oy+(nd.ny-nd.oy)*k; nd.z=nd.oz+(nd.nz-nd.oz)*k; }
      if(nd.born<1) nd.born=Math.min(1,nd.born+dt*1.5);
      if(nd.state===1 && nd.st>2.2){ nd.state=2; nd.st=0; }
      else if(nd.state===2 && nd.st>5.5){ nd.state=0; nd.st=0; }
    }
    for(const ag of this._agents){
      ag.p+=ag.v*dt;
      if(ag.p>=1){ const [,b]=E[ag.e]; const nd=N[b];
        if(nd.state!==1){ nd.state=1; nd.st=0; }
        const out=E.map((e,i)=>e[0]===b?i:-1).filter(i=>i>=0);
        ag.e = out.length&&Math.random()<0.8 ? out[Math.floor(Math.random()*out.length)]
             : Math.floor(Math.random()*E.length)|0;
        if(!out.length||Math.random()>=0.8){ // restart near source layers
          const cands=E.map((e,i)=>N[e[0]].layer<2?i:-1).filter(i=>i>=0);
          if(cands.length) ag.e=cands[Math.floor(Math.random()*cands.length)];
        }
        ag.p=0; ag.v=0.25+Math.random()*0.4;
      }
    }
    if(this._rewireT>2.4){ this._rewireT=0;
      const nd=N[Math.floor(Math.random()*N.length)];
      nd.oy=nd.y; nd.oz=nd.z; nd.ny=(Math.random()*2-1)*0.95; nd.nz=(Math.random()*2-1)*0.9;
      nd.lerp=0; nd.born=0; nd.state=0; nd.st=0;
      // occasionally rewire one of its edges
      const idx=E.findIndex(e=>e[0]===N.indexOf(nd));
      if(idx>=0 && nd.layer<this._layers.length-1){
        const next=this._layers[nd.layer+1];
        E[idx][1]=next[Math.floor(Math.random()*next.length)];
      }
    }
  }
  _draw(){
    const gl=this._gl, loc=this._loc;
    const ink=hex(this.getAttribute('ink'),[0.09,0.07,0.05]);
    const accent=hex(this.getAttribute('accent'),[0.78,0.31,0.12]);
    gl.clearColor(0,0,0,0); gl.clear(gl.COLOR_BUFFER_BIT);
    gl.uniform1f(loc.rotY, this._t*0.07);
    gl.uniform1f(loc.rotX, 0.28+Math.sin(this._t*0.05)*0.05);
    gl.uniform1f(loc.aspect, this._aspect||1);
    gl.uniform1f(loc.dpr, this._dpr||1);
    const N=this._nodes;
    const px=(nd)=>nd.x, py=(nd)=>nd.y+0.045*Math.sin(this._t*nd.f+nd.ph), pz=(nd)=>nd.z+0.045*Math.cos(this._t*nd.f*0.8+nd.ph);
    // lines
    const lv=[];
    for(const [a,b] of this._edges){
      const A=N[a],B=N[b];
      const act=(A.state===1||B.state===1);
      const col=act?accent:ink;
      const al=(act?0.5:0.13)*Math.min(A.born,B.born);
      lv.push(px(A),py(A),pz(A),col[0],col[1],col[2],al,1, px(B),py(B),pz(B),col[0],col[1],col[2],al,1);
    }
    this._emit(lv, gl.LINES, 0);
    // node points
    const pv=[];
    for(const nd of N){
      let col=ink, al=0.28, sz=3.5;
      if(nd.state===1){ col=accent; al=0.95; sz=7+2*Math.sin(this._t*6+nd.ph); }
      else if(nd.state===2){ al=0.6; sz=4.5; }
      al*=nd.born;
      pv.push(px(nd),py(nd),pz(nd),col[0],col[1],col[2],al,sz);
    }
    // agents + trails
    for(const ag of this._agents){
      const [a,b]=this._edges[ag.e], A=N[a],B=N[b];
      for(let k=0;k<4;k++){
        const p=Math.max(0,ag.p-k*0.045);
        const x=px(A)+(px(B)-px(A))*p, y=py(A)+(py(B)-py(A))*p, z=pz(A)+(pz(B)-pz(A))*p;
        pv.push(x,y,z,accent[0],accent[1],accent[2],(k===0?1:0.35-k*0.08),(k===0?5.5:3.5-k*0.6));
      }
    }
    this._emit(pv, gl.POINTS, 1);
  }
  _emit(arr, mode, isPoint){
    const gl=this._gl, loc=this._loc;
    gl.uniform1f(loc.point,isPoint);
    gl.bindBuffer(gl.ARRAY_BUFFER,this._buf);
    gl.bufferData(gl.ARRAY_BUFFER,new Float32Array(arr),gl.DYNAMIC_DRAW);
    const S=8*4;
    gl.enableVertexAttribArray(loc.pos); gl.vertexAttribPointer(loc.pos,3,gl.FLOAT,false,S,0);
    gl.enableVertexAttribArray(loc.col); gl.vertexAttribPointer(loc.col,4,gl.FLOAT,false,S,12);
    gl.enableVertexAttribArray(loc.size); gl.vertexAttribPointer(loc.size,1,gl.FLOAT,false,S,28);
    gl.drawArrays(mode,0,arr.length/8);
  }
}
customElements.define('hero-dag',HeroDag);
})();
