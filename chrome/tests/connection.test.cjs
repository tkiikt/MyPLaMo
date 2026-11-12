const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm'),fs=require('node:fs'),path=require('node:path');
function fixture(){
 const requests=[],icons=[],titles=[],canvases=[];let online=false,translateError;
 const sandbox={AbortSignal,fetch:async url=>{requests.push(url);if(url.startsWith('chrome-extension:'))return {blob:async()=>({})};if(!online)throw new Error('Connection refused');return {status:403};},
  createImageBitmap:async()=>({close(){}}),OffscreenCanvas:class{constructor(width,height){this.width=width;this.height=height;canvases.push(this);}getContext(){return {drawImage(){},beginPath(){},arc:(x,y,r)=>{this.circle={x,y,r};},fill(){},stroke(){},fillRect(){},getImageData:()=>({width:this.width,height:this.height})};}},
  chrome:{runtime:{getURL:route=>'chrome-extension://self/'+route},action:{setIcon:async value=>icons.push(value),setTitle:async value=>titles.push(value.title),setBadgeText:async()=>{}}},
  bridgeRequest:async message=>{if(translateError)throw translateError;return {translation:message.text};}};
 vm.createContext(sandbox);vm.runInContext(fs.readFileSync(path.join(__dirname,'../extension/connection.js'),'utf8')+'\nglobalThis.connection=MyPLaMoConnection;',sandbox);
 return {connection:sandbox.connection,requests,icons,titles,canvases,setOnline:value=>{online=value;},setError:error=>{translateError=error;}};
}
test('offline checks render a yellow warning icon without opening a WebSocket',async()=>{
 const f=fixture();assert.equal(await f.connection.refresh(),false);
 assert.ok(f.icons[0].imageData[32]);assert.match(f.titles[0],/未接続/);
 await assert.rejects(f.connection.request({text:'Hello'}),error=>error.code==='APP_OFFLINE');
 assert.equal(f.icons.length,1);assert.equal(f.canvases.length,5);
 assert.ok(f.canvases.every(c=>c.circle.x>c.width/2&&c.circle.y>c.height/2&&c.circle.x+c.circle.r<c.width));
});
test('connection recovery restores original icon and translations resume',async()=>{
 const f=fixture();await f.connection.refresh();f.setOnline(true);
 assert.equal(await f.connection.refresh(),true);assert.equal(f.icons.at(-1).path[32],'icons/myplamo-32.png');
 assert.deepEqual(await f.connection.request({text:'Hello'}),{translation:'Hello'});
 f.setOnline(false);assert.equal(await f.connection.refresh(),false);assert.ok(f.icons.at(-1).imageData[32]);
});
test('ordinary server translation errors keep the connection icon normal',async()=>{
 const f=fixture();f.setOnline(true);f.setError(new Error('Model not selected'));
 await assert.rejects(f.connection.request({text:'Hello'}),/Model not selected/);assert.ok(f.icons.at(-1).path);
 f.setError(Object.assign(new Error('Connection lost'),{code:'APP_OFFLINE'}));
 await assert.rejects(f.connection.request({text:'Hello'}),/Connection lost/);assert.ok(f.icons.at(-1).imageData);
});
