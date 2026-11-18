namespace ShareLink.BrowserSharing;

internal static class BrowserListing
{
    internal const string Script = """
(() => {
  const list = document.getElementById('shared-files');
  const sessionId = document.querySelector('main').dataset.sharelinkSession || '';
  let ended = false;
  function expire() {
    if(ended) return; ended=true; list.replaceChildren();
    let notice=document.getElementById('sharelink-session-status');
    if(!notice){notice=document.createElement('p');notice.id='sharelink-session-status';notice.setAttribute('role','status');list.before(notice);}
    notice.textContent='本次分享已结束，请重新打开分享页面。';
    window.dispatchEvent(new Event('sharelink-session-ended'));
  }
  window.addEventListener('sharelink-session-ended',expire);
  let revision = -1, polling = false;
  function size(bytes) { const units=['B','KB','MB','GB','TB']; let value=bytes,unit=0; while(value>=1024&&unit<4){value/=1024;unit++;} return unit===0?bytes+' B':value.toFixed(value<10?2:1)+' '+units[unit]; }
  async function sync() {
    if(polling || ended) return; polling=true;
    try {
      const response=await fetch('/manifest.json'+(sessionId?'?session='+encodeURIComponent(sessionId):''),{cache:'no-store'});
      if(response.status===410){expire();return;}
      if(!response.ok) return;
      const data=await response.json();
      if(sessionId && data.sessionId!==sessionId){expire();return;}
      if(data.revision===revision) return;
      const wanted=new Set(data.files.map(file=>file.id));
      for(const node of Array.from(list.children)) if(!wanted.has(node.dataset.fileId)) node.remove();
      const existing=new Map(Array.from(list.children).map(node=>[node.dataset.fileId,node]));
      for(const file of data.files) {
        if(existing.has(file.id)) continue;
        const item=document.createElement('li'); item.dataset.fileId=file.id;
        const name=document.createElement('div'); name.textContent=file.name;
        const detail=document.createElement('small'); detail.textContent=size(file.size);
        const link=document.createElement('a'); link.href='/files/'+file.id; link.download=''; link.textContent='下载';
        item.append(name,detail,link); list.append(item);
      }
      revision=data.revision;
    } catch(_) {} finally { polling=false; }
  }
  async function poll() { await sync(); setTimeout(poll,1000); }
  document.addEventListener('visibilitychange',()=>{if(!document.hidden)sync();});
  poll();
})();
""";
}
