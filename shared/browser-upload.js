(() => {
  const panel=document.createElement('section'), button=document.createElement('button'), input=document.createElement('input'), status=document.createElement('p'), progress=document.createElement('progress'), detail=document.createElement('p');
  button.textContent='上传文件';button.type='button';input.type='file';input.multiple=true;input.hidden=true;
  progress.id='sharelink-upload-progress';progress.max=100;progress.value=0;progress.hidden=true;progress.style.width='100%';progress.setAttribute('aria-label','文件上传进度');
  detail.id='sharelink-upload-detail';status.id='sharelink-upload-status';status.setAttribute('role','status');
  const heading=document.querySelector('main h1'), header=document.createElement('div');header.className='sharelink-page-header';heading.before(header);header.append(heading,button);button.hidden=true;
  panel.hidden=true;panel.className='sharelink-upload-panel';panel.append(input,progress,detail,status);header.after(panel);
  const sessionId=document.querySelector('main').dataset.sharelinkSession||'';
  let token='',busy=false,ended=false;
  function expire(){if(ended)return;ended=true;token='';button.disabled=true;button.hidden=true;panel.hidden=true;status.textContent='本次分享已结束，请重新打开分享页面。';window.dispatchEvent(new Event('sharelink-session-ended'));}
  window.addEventListener('sharelink-session-ended',expire);
  function size(bytes){if(bytes<1024)return Math.round(bytes)+' B';if(bytes<1048576)return (bytes/1024).toFixed(1)+' KB';if(bytes<1073741824)return (bytes/1048576).toFixed(1)+' MB';return (bytes/1073741824).toFixed(2)+' GB';}
  async function update(){if(ended)return;try{const response=await fetch('/manifest.json'+(sessionId?'?session='+encodeURIComponent(sessionId):''),{cache:'no-store'});if(response.status===410){expire();return;}if(!response.ok)return;const data=await response.json();if(sessionId&&data.sessionId!==sessionId){expire();return;}token=data.uploadToken||'';button.hidden=!token&&!busy;panel.hidden=!busy&&!status.textContent;button.disabled=busy||!token;}catch(_){}}
  function upload(file,sessionToken,index,total){return new Promise((resolve,reject)=>{
    const xhr=new XMLHttpRequest();let sent=0;const samples=[{at:performance.now(),bytes:0}];let rateTimer;
    function stopRate(){if(rateTimer!==undefined){clearInterval(rateTimer);rateTimer=undefined;}}
    progress.hidden=false;progress.value=0;detail.textContent='0% · '+size(0)+' / '+size(file.size)+' · 0 B/s';status.textContent=`正在上传（${index}/${total}）：${file.name}`;
    function report(loaded){sent=Math.max(sent,Math.min(file.size,loaded));const now=performance.now();samples.push({at:now,bytes:sent});while(samples.length>2&&samples[1].at<=now-1000)samples.shift();const first=samples[0],elapsed=now-first.at,rate=elapsed>0?Math.max(0,(sent-first.bytes)*1000/elapsed):0;const percent=file.size?Math.round(sent*100/file.size):100;progress.value=percent;detail.textContent=`${percent}% · ${size(sent)} / ${size(file.size)} · ${size(rate)}/s`;}
    xhr.open('POST','/upload');xhr.responseType='json';if(sessionId)xhr.setRequestHeader('X-ShareLink-Session',sessionId);xhr.setRequestHeader('X-ShareLink-Upload',sessionToken);xhr.setRequestHeader('X-ShareLink-Filename',encodeURIComponent(file.name));xhr.setRequestHeader('Content-Type','application/octet-stream');
    xhr.upload.onprogress=event=>report(event.loaded);
    xhr.upload.onload=()=>{stopRate();report(file.size);status.textContent='数据已上传，等待对端保存：'+file.name;};
    xhr.onload=()=>{stopRate();if(xhr.status===410){expire();reject(Error('本次分享已结束，请重新打开分享页面。'));return;}if(xhr.status<200||xhr.status>=300){reject(Error(xhr.status===507?'对端无法保存文件，请检查接收文件夹或可用空间后重试':'上传失败（'+xhr.status+'），请确认对端仍允许接收'));return;}if(!xhr.response||typeof xhr.response.name!=='string'){reject(Error('对端未返回保存结果，请检查接收设备'));return;}report(file.size);resolve(xhr.response);};
    xhr.onerror=()=>{stopRate();reject(Error('上传连接已中断，请确认分享仍在开启后重试'));};xhr.onabort=()=>{stopRate();reject(Error('上传已取消'));};xhr.ontimeout=()=>{stopRate();reject(Error('上传超时，请重试'));};rateTimer=setInterval(()=>report(sent),500);xhr.send(file);
  });}
  button.onclick=()=>input.click();
  input.onchange=async()=>{if(ended||busy||!token)return;busy=true;panel.hidden=false;button.disabled=true;const files=Array.from(input.files),sessionToken=token;let saved=0,current;
    try{for(let i=0;i<files.length;i++){const file=files[i];current=file;const result=await upload(file,sessionToken,i+1,files.length);saved++;status.textContent=files.length>1?`已保存到对端（${saved}/${files.length}）：${result.name}`:'已保存到对端：'+result.name;}}
    catch(error){status.textContent=ended?'本次分享已结束，请重新打开分享页面。':files.length>1?`已保存 ${saved}/${files.length} 个文件。${current.name}：${error.message}。后续 ${files.length-saved-1} 个文件未上传，请仅选择失败及未上传文件重试。`:error.message;}finally{busy=false;button.disabled=ended||!token;input.value='';}};
  update();setInterval(update,2000);
})();
