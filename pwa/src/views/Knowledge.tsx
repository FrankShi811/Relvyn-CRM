import { FileText, Search, Trash2, Upload } from "lucide-react";
import { useRef, useState } from "react";
import { retrieveKnowledge, uid } from "../domain";
import { useStore } from "../store";
import { Button, Card, EmptyState, PageHeader } from "../components/ui";

export function Knowledge() {
  const { knowledge, saveKnowledge, removeKnowledge } = useStore();
  const [query, setQuery] = useState("");
  const [notice, setNotice] = useState("");
  const fileRef = useRef<HTMLInputElement>(null);
  const hits = query.trim() ? retrieveKnowledge(knowledge, query) : [];
  const upload = async (file?: File) => {
    if (!file) return;
    try {
      let text = "";
      if (/\.(xlsx|xls|csv)$/i.test(file.name)) {
        const XLSX = await import("xlsx");
        const book = XLSX.read(await file.arrayBuffer(), { type: "array" });
        text = book.SheetNames.map(name => `${name}\n${XLSX.utils.sheet_to_csv(book.Sheets[name])}`).join("\n\n");
      } else {
        text = await file.text();
        if (/\.html?$/i.test(file.name)) text = new DOMParser().parseFromString(text, "text/html").body.innerText;
      }
      if (!text.trim()) throw new Error("没有读取到可用文本。纯 PWA 当前支持 TXT、Markdown、CSV、JSON、HTML 和 Excel。");
      await saveKnowledge({ id: uid(), name: file.name, category: "业务资料", text: text.slice(0, 2_000_000), enabled: true, createdAt: new Date().toISOString() });
      setNotice(`已启用：${file.name}`);
    } catch (e) { setNotice(e instanceof Error ? e.message : "读取失败"); }
  };
  return <>
    <PageHeader title="知识库" subtitle="把产品与业务资料保存在浏览器中，供本地检索和 AI 草稿引用。"
      actions={<Button onClick={() => fileRef.current?.click()}><Upload size={16}/>上传知识</Button>}/>
    <input hidden ref={fileRef} type="file" aria-label="上传知识文件" accept=".txt,.md,.csv,.json,.html,.htm,.xlsx,.xls" onChange={e => void upload(e.target.files?.[0])}/>
    {notice && <div className="notice" role="status">{notice}<button aria-label="关闭提示" onClick={() => setNotice("")}>×</button></div>}
    <section className="knowledge-layout">
      <Card className="knowledge-list"><div className="section-title"><div><h2>已启用资料</h2><p>{knowledge.length} 份 · 当前浏览器</p></div></div>
        {!knowledge.length ? <EmptyState title="还没有知识资料" body="上传 TXT、Markdown、CSV、JSON、HTML 或 Excel 文件。"/> :
        knowledge.map(doc => <div className="knowledge-row" key={doc.id}><span><FileText/></span><div><strong>{doc.name}</strong><small>{doc.text.length.toLocaleString()} 字符 · {new Date(doc.createdAt).toLocaleDateString("zh-CN")}</small></div><button aria-label={`删除 ${doc.name}`} onClick={() => void removeKnowledge(doc.id)}><Trash2 size={16}/></button></div>)}
      </Card>
      <Card className="retrieval-test"><h2>本地检索测试</h2><p>确认 AI 写作前能找到哪些已批准资料。</p>
        <div className="search"><Search/><input aria-label="检索知识库" name="knowledge-search" autoComplete="off" value={query} onChange={e => setQuery(e.target.value)} placeholder="输入产品、政策或客户问题…"/></div>
        <div className="retrieval-results">{query && !hits.length && <span className="muted-copy">没有命中已启用资料。</span>}{hits.map(hit => <article key={hit.doc.id}><strong>{hit.doc.name}</strong><span>相关词命中 {hit.score}</span><p>{hit.doc.text.slice(0, 320)}…</p></article>)}</div>
      </Card>
    </section>
  </>;
}
