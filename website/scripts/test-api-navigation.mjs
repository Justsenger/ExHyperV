import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import test from 'node:test';

// Execute the exported page's real router and renderer without a browser.
const html = await readFile(process.env.API_DOCUMENT_HTML || new URL('../public/api/index.html', import.meta.url), 'utf8');
const data = JSON.parse(html.match(/<script[^>]*id="api-data"[^>]*>([\s\S]*?)<\/script>/)[1]);
const script = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].at(-1)[1];
const titleScript = html.match(/<script id="api-title">([\s\S]*?)<\/script>/)?.[1];

function page({ hash = '', languages = ['zh-CN'] } = {}) {
  const nodes = new Map(), events = new Map(), documentEvents = new Map();
  let scrolled, scrollY = 0;
  class Element {
    constructor(id) { this.id = id; this.value = ''; this.events = new Map(); this.children = []; this.attrs = new Map(); }
    set innerHTML(value) {
      for (const id of this.children) nodes.delete(id);
      this.html = value;
      this.children = [...value.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]);
      for (const id of this.children) nodes.set(id, new Element(id));
    }
    get innerHTML() { return this.html || ''; }
    setAttribute(name,value) { this.attrs.set(name,value); }
    addEventListener(name,callback) { this.events.set(name,callback); }
    focus() {}
    scrollIntoView() { scrolled = this.id; }
    querySelectorAll() { return []; }
  }
  for (const match of html.slice(0,html.indexOf('<script type="application/json"')).matchAll(/\bid="([^"]+)"/g))
    nodes.set(match[1],new Element(match[1]));
  nodes.set('api-data',{textContent:JSON.stringify(data)});
  const location = {hash};
  const context = {document:{getElementById:id=>nodes.get(id),addEventListener:(name,callback)=>documentEvents.set(name,callback),
    body:{classList:{contains:()=>false,remove(){},toggle:()=>false}}},
    location, navigator:{languages}, window:{addEventListener:(name,callback)=>events.set(name,callback),scrollTo(x,y){scrollY=y;}},
    requestAnimationFrame:callback=>callback(), HTMLElement:Element};
  if (titleScript) vm.runInNewContext(titleScript,context,{timeout:1000});
  vm.runInNewContext(script,context,{timeout:10000});
  return {
    open(id) { scrolled=undefined;location.hash='#'+id;events.get('hashchange')();return nodes.get('main-content').innerHTML; },
    clickCurrentLink(options={}) {
      let prevented=false;
      documentEvents.get('click')({button:0,...options,
        target:{closest:()=>({getAttribute:()=>location.hash,hasAttribute:()=>false,target:''})},
        preventDefault(){prevented=true;}});
      return prevented;
    },
    setScroll(y) { scrollY=y; },
    setLanguage(language) { context.navigator.languages=[language];events.get('languagechange')?.(); },
    search(text) { const input=nodes.get('api-search');input.value=text;input.events.get('input')();return nodes.get('navigation').innerHTML; },
    get scrollY() { return scrollY; },
    get navigation() { return nodes.get('navigation').innerHTML; },
    get content() { return nodes.get('main-content').innerHTML; },
    get title() { return context.document.title; },
    get breadcrumb() { return nodes.get('breadcrumb-current').textContent; },
    get target() { return scrolled; },
  };
}

const capabilityCategories = data.categories.filter(category => category.path[0]==='管理层面'
  && ['虚拟机','宿主'].includes(category.path[1]) && ['查询','修改'].includes(category.path[2]) && category.path.length===4);
const categoryKey = category => `${category.path[1]}/${category.path[3]}`;
const topics = [...new Map(capabilityCategories.map(category=>[categoryKey(category),category])).values()];
const members = data.types.flatMap(type=>type.members.map(member=>({type,member})));
const member = (owner,name) => data.types.find(type=>type.shortName===owner).members.find(item=>item.name===name);
const stripLinks = value => value.replace(/<\/?a\b[^>]*>/g,'');

test('the API home is a concise overview with seven working category buttons', () => {
  const view = page();
  assert.equal(view.breadcrumb, '概览');
  const content = view.content;
  assert.match(content, /<h1>概览<\/h1>/);
  assert.doesNotMatch(content, /管理虚拟机，连接来宾。|虚拟机、宿主与文件工具|连接、画面与输入交互/);
  assert.match(content, /class="overview-columns"/);
  assert.match(content, /id="overview-management">管理层面/);
  assert.match(content, /id="overview-business">业务层面/);
  assert.doesNotMatch(content, /<table|member-card|type-overview|使用说明/);
  const buttons = [...content.matchAll(/href="#([^"]+)" class="overview-entry"/g)].map(match => match[1]);
  assert.equal(buttons.length, 7);
  assert.equal(new Set(buttons).size, 7);
  for (const id of buttons) {
    const destination = view.open(id);
    assert.notEqual(view.breadcrumb, '概览', id);
    assert.doesNotMatch(destination, /未找到文档/, id);
    assert.ok(destination.includes(`id="${id}"`), id);
    if (id.startsWith('category-')) {
      assert.doesNotMatch(destination, /member-card|type-overview/);
      for (const link of destination.matchAll(/href="#([^"]+)" class="category-card"/g)) {
        const target = view.open(link[1]);
        assert.doesNotMatch(target, /未找到文档/, link[1]);
        assert.notEqual(view.breadcrumb, '概览', link[1]);
        assert.ok(target.includes(`id="${link[1]}"`), link[1]);
      }
    }
  }
});

test('document title stays fixed across routes and follows browser language changes', () => {
  assert.match(html, /<title>API 文档<\/title>/);
  assert.ok(titleScript);
  const routes = ['overview','category-tools',data.types[0].id,data.types[0].members[0].id,data.designs[0].id,'missing-route'];
  const view = page();
  for (const [language, title] of [['zh-CN','API 文档'],['en-US','API Documentation'],['zh-TW','API 文件'],['zh-HK','API 文件'],['zh-CN','API 文档']]) {
    view.setLanguage(language);
    assert.equal(view.title,title);
    for (const id of routes) { view.open(id);assert.equal(view.title,title,id); }
    assert.equal(page({languages:[language]}).title,title,'initial language');
  }
});

test('partition parsing is reachable from tools and GPU without exposing the internal parser', () => {
  const view = page();
  const category = data.categories.find(item => item.path.join('/') === '管理层面/工具/磁盘分区解析');
  assert.ok(category);
  assert.ok(view.open('category-tools').includes(`href="#${category.id}"`));
  const content = view.open(category.id);
  const scan = member('VmGpuService','ScanVmPartitionsAsync');
  assert.ok(content.includes(`id="${scan.id}"`));
  assert.ok(content.includes('VmPartitionScanResult'));
  const gpu = topics.find(item => categoryKey(item) === '虚拟机/GPU');
  assert.ok(view.open(gpu.id).includes(`id="${scan.id}"`));
  assert.ok(!data.types.some(item => item.shortName === 'DiskParser'));
});

test('the separate guide section is removed while API examples and result types remain accessible', async () => {
  assert.doesNotMatch(html.slice(0,html.indexOf('<script type="application/json"')), /guide-tab|使用说明/);
  assert.equal(data.guides.length,0);
  const view = page();
  for (const id of ['guide','guide-开始使用','guide-查询数据','guide-结果取消和资源释放']) {
    assert.doesNotMatch(view.open(id), /未找到文档|guide-article/);
    assert.doesNotMatch(view.navigation, /使用说明/);
  }
  const clipboard = data.types.find(item => item.shortName === 'IConsoleClipboard');
  assert.ok(view.open(clipboard.id).includes('ClipboardExample'));
  const response = data.types.find(item => item.shortName === 'ApiResponse');
  const responsePage = view.open(response.id);
  assert.ok(responsePage.includes(`id="${view.target || response.id}"`));
  const examples = await readFile(new URL('../../doc/api/UsageExamples.cs',import.meta.url),'utf8');
  const expected = [...examples.matchAll(/\/\/ example:([\w-]+):start/g)].map(match => match[1]).sort();
  const actual = [];
  for (const {member:item} of members) {
    for (const match of item.presentation.examplesHtml.matchAll(/data-example="([^"]+)"/g)) {
      actual.push(match[1]);
      assert.ok(view.open(item.id).includes(`data-example="${match[1]}"`), match[1]);
    }
  }
  assert.deepEqual(actual.sort(),expected);
  for (const type of data.types.filter(item => item.examplesHtml)) {
    assert.ok(stripLinks(view.open(type.id)).includes(stripLinks(type.examplesHtml)),type.shortName);
  }
});

test('XML documentation IDs stay out of visible reference labels', () => {
  let checked = 0;
  function inspect(value, location = 'data') {
    if (!value || typeof value !== 'object') return;
    for (const [key, item] of Object.entries(value)) {
      if (key.endsWith('Html') || key === 'html') {
        if (typeof item !== 'string') continue;
        const visible = item.replace(/<[^>]+>/g, '');
        assert.doesNotMatch(visible, /\b[TMFPE]:ExHyperV\./, `${location}.${key}`);
        checked++;
      } else if (typeof item === 'object') inspect(item, `${location}.${key}`);
    }
  }
  inspect(data);
  assert.ok(checked > 1900, 'inspect the complete generated reference');
});

test('memory write links use short names and still resolve to the exact method', () => {
  const target = member('VmMemoryService', 'SetVmMemorySettingsAsync');
  const view = page();
  for (const name of ['Startup', 'DynamicMemoryEnabled']) {
    const field = member('VmMemoryFields', name);
    assert.match(field.modification.membersHtml, /<code>VmMemoryService\.SetVmMemorySettingsAsync<\/code>/);
    assert.ok(field.modification.membersHtml.includes(`href="#${target.id}"`));
    assert.ok(view.open(field.id).includes(`href="#${target.id}"`));
  }
  const destination = view.open(target.id);
  assert.ok(destination.includes(`id="${view.target || target.id}"`));
});

function elementAt(content,id) {
  const position=content.indexOf(`id="${id}"`);
  assert.ok(position>=0,`missing element: ${id}`);
  const start=content.lastIndexOf('<',position);
  const tag=content.slice(start).match(/^<(\w+)\b/)[1];
  const tags=new RegExp(`<\\/?${tag}\\b[^>]*>`,'g');
  tags.lastIndex=start;
  let depth=0;
  for(let match;(match=tags.exec(content));) {
    depth+=match[0].startsWith('</')?-1:1;
    if(depth===0)return content.slice(start,tags.lastIndex);
  }
  assert.fail(`unclosed element: ${id}`);
}

test('enum type and value links lead to the field that owns their explanation', () => {
  const view=page();
  for(const type of data.types.filter(type=>type.kind==='枚举')) {
    const fields=members.filter(({member:item})=>item.queryField?.enumTypeId===type.id);
    if(fields.length!==1) continue;
    const field=fields[0].member;
    const content=view.open(type.id);
    assert.ok(content.includes(`id="${field.id}"`),type.shortName);
    assert.equal(view.target,field.id,type.shortName);
    assert.ok(!content.includes(`href="#${type.id}"`),`duplicate enum page: ${type.shortName}`);
    for(const value of type.members.filter(item=>item.kind==='枚举值')) {
      const anchor=`${field.id}--${value.id}`;
      assert.equal(content.split(`id="${anchor}"`).length-1,1,value.name);
      view.open(value.id);
      assert.equal(view.target,anchor,value.name);
    }
    view.open(type.id+'--members');
    assert.equal(view.target,field.id);
  }
});

test('settings reuse field value explanations instead of repeating enum tables', () => {
  const view=page();
  const settings=data.types.find(type=>type.shortName==='VmProcessorSettings');
  const field=member('VmProcessorFields','L3DistributionPolicy');
  const property=member('VmProcessorSettings','L3DistributionPolicy');
  const content=view.open(property.id);
  assert.equal(view.target,field.id);
  const card=elementAt(content,field.id);
  assert.ok(card.includes('VmProcessorSettings.L3DistributionPolicy'));
  const settingsContent=view.open(settings.id);
  assert.ok(settingsContent.includes('配置项见'));
  const block=elementAt(settingsContent,view.target);
  assert.doesNotMatch(block,/SmallToLarge\s*=|LargeToSmall\s*=/);
  const search=view.search('L3DistributionPolicy');
  assert.equal(search.split(`href="#${field.id}"`).length-1,1,'duplicate search destination');
});

test('sidebar lists each capability once, with no query/write branches', () => {
  const view=page();
  assert.doesNotMatch(view.navigation,/<summary>(查询|修改)<\/summary>/);
  const links=[...view.navigation.matchAll(/href="#([^"]+)"/g)].map(match=>match[1]);
  for(const topic of topics) {
    const aliases=capabilityCategories.filter(c=>categoryKey(c)===categoryKey(topic)).map(c=>c.id);
    assert.equal(links.filter(id=>aliases.includes(id)).length,1,categoryKey(topic));
  }
});

test('all capability pages put fields before methods and contain no duplicate anchors', () => {
  const view=page();
  for(const topic of topics) {
    const content=view.open(topic.id);
    assert.equal(view.breadcrumb,`${topic.path[1]} / ${topic.path[3]}`);
    assert.doesNotMatch(content,/未找到文档/);
    const ids=[...content.matchAll(/\bid="([^"]+)"/g)].map(match=>match[1]);
    assert.equal(ids.length,new Set(ids).size,`duplicate anchor: ${categoryKey(topic)}`);
    const fields=content.indexOf('>字段</h2>'), methods=content.indexOf('>方法</h2>');
    if(fields>=0 && methods>=0) assert.ok(fields<methods,categoryKey(topic));
    const originals=capabilityCategories.filter(c=>categoryKey(c)===categoryKey(topic));
    for(const source of originals) assert.equal(view.open(source.id),content,`legacy route: ${source.id}`);
  }
});

test('basic information combines identity fields, querying, and rename methods', () => {
  const view=page();
  const topic=topics.find(c=>categoryKey(c)==='虚拟机/基本信息');
  const content=view.open(topic.id);
  for(const owner of ['VmIdentityFields','VmEditService']) {
    for(const item of data.types.find(type=>type.shortName===owner).members)
      assert.ok(content.includes(`id="${item.id}"`),`${owner}.${item.name}`);
  }
  const query=member('VmQueryService','QueryAsync');
  assert.ok(content.indexOf(`id="${query.id}"`)>content.indexOf('>方法</h2>'));
  view.open(query.id);
  assert.equal(view.breadcrumb,'虚拟机 / 基本信息');
  assert.equal(view.target,query.id);
});

test('basic information keeps the shared query protocol linked instead of recursively expanding it', () => {
  const view=page();
  const topic=topics.find(c=>categoryKey(c)==='虚拟机/基本信息');
  const content=view.open(topic.id);
  assert.doesNotMatch(content,/class="inline-contract"|class="contract-fields"|class="contract-reference"|field-member-reference/);
  const methods=content.slice(content.indexOf('class="capability-methods"'));
  assert.deepEqual([...methods.matchAll(/<h3[^>]*>([^<]+)<\/h3>/g)].map(match=>match[1]),['读取字段','重命名']);
  const query=elementAt(content,member('VmQueryService','QueryAsync').id);
  for (const name of ['VmQueryRequest','VmQueryResult']) {
    const type=data.types.find(type=>type.shortName===name);
    assert.ok(query.includes(`href="#${type.id}"`),name);
    const details=view.open(type.id);
    assert.ok(details.includes(`id="${type.id}"`),name);
    assert.doesNotMatch(details,/class="capability-fields"/);
  }
});

test('affinity read results link to existing fields without nesting member cards', () => {
  const view=page();
  const read=member('CpuAffinityService','ReadStateAsync');
  const result=data.types.find(type=>type.shortName==='CpuAffinityService.AffinityState');
  const card=elementAt(view.open(read.id),read.id);
  assert.equal((card.match(/class="field-reference"/g)||[]).length,1);
  assert.equal((card.match(/class="contract-fields"/g)||[]).length,1);
  assert.doesNotMatch(card,/>创建实例<|<h[45]/);
  for (const property of result.members.filter(item=>!item.presentation.callable)) {
    const field=member(property.name==='State'?'VmIdentityFields':'VmAffinityFields',property.name);
    assert.ok(card.includes(`href="#${field.id}"`),property.name);
    const target=view.open(property.id);
    assert.equal(view.target,field.id);
    assert.ok(target.includes(`id="${field.id}"`));
  }
  for (const constructor of result.members.filter(item=>item.presentation.callable)) {
    const content=view.open(constructor.id);
    assert.ok(content.includes(`id="${constructor.id}"`));
    assert.equal(view.scrollY,0);
    assert.equal((content.match(/class="field-reference"/g)||[]).length,1);
  }
});

test('basic information puts descriptions first and distinguishes writable, read-only and operation-driven fields', () => {
  const view=page();
  const topic=topics.find(c=>categoryKey(c)==='虚拟机/基本信息');
  const content=view.open(topic.id);
  const cardFor=item=>{
    const start=content.indexOf(`id="${item.id}"`);
    return content.slice(start,content.indexOf('</section>',start));
  };
  const identity=data.types.find(type=>type.shortName==='VmIdentityFields');
  for (const item of identity.members) {
    const card=cardFor(item);
    assert.match(card,/<dl class="field-reference"><div class="field-description"><dt>描述<\/dt>/,item.name);
    assert.ok(card.includes('<dt>属性</dt>'),item.name);
    for (const id of item.modification.memberIds) assert.ok(card.includes(`href="#${id}"`),item.name);
  }
  assert.match(cardFor(member('VmIdentityFields','Name')), /读写，可<a href="[^"]+">重命名<\/a>。/);
  assert.match(cardFor(member('VmIdentityFields','State')), /只读。可通过<a href="[^"]+">电源操作<\/a>以及虚拟机内部改变。/);
  for (const name of ['Id','Uptime','Generation']) assert.match(cardFor(member('VmIdentityFields',name)),/只读/);
  assert.ok(cardFor(member('VmIdentityFields','Id')).includes('只读。在创建或导入时确定。'));
  assert.ok(cardFor(member('VmIdentityFields','Generation')).includes('只读。创建时选定。'));
  for (const name of ['Uptime','ConfigurationVersion','ConfigurationFilePath','GuestStateFilePath','RuntimeStateFilePath']) {
    const item=member('VmIdentityFields',name);
    assert.equal(item.modification.html,'');
    assert.match(cardFor(item),/<dt>属性<\/dt><dd>只读<\/dd>/);
  }
  assert.doesNotMatch(content,/未提供|不提供原地切换|检查名称/);
  for (const item of [member('VmQueryService','QueryAsync'),...data.types.find(t=>t.shortName==='VmEditService').members])
    assert.match(cardFor(item),/<dl class="field-reference"><div class="field-description"><dt>描述<\/dt>/,item.name);
  // Every query field with a modification contract must expose it, including its conditions.
  for (const {member:item} of members.filter(({member:item})=>item.queryField && item.modification)) {
    const pageHtml=view.open(item.id);
    const start=pageHtml.indexOf(`id="${item.id}"`);
    const card=pageHtml.slice(start,pageHtml.indexOf('</section>',start));
    assert.ok(card.includes('<dt>属性</dt>'),item.queryField.expression);
    assert.match(card,/<dt>属性<\/dt><dd>(只读|读写)(。|，|<\/dd>)/,item.queryField.expression);
    const withoutLinks=html=>html.replace(/<a\b[^>]*>[\s\S]*?<\/a>/g,'');
    if (item.modification.html) assert.ok(withoutLinks(card).includes(withoutLinks(item.modification.html)),item.queryField.expression);
  }
});

test('every existing management method and field remains reachable in its capability', () => {
  const view=page();
  for(const topic of topics) {
    const content=view.open(topic.id);
    const paths=capabilityCategories.filter(c=>categoryKey(c)===categoryKey(topic)).map(c=>c.area);
    for(const {type,member:item} of members) {
      if(!(item.navigationPaths||[]).some(path=>paths.includes(path.join(' / ')))) continue;
      const method=item.presentation.callable && !['构造函数','构造器','主构造器','运算符'].includes(item.kind);
      if(!method && !item.queryField && !item.hostEntry) continue;
      if(method && type.role==='contract' && !item.queryField && !item.hostEntry) {
        const details=view.open(item.id);
        assert.ok(view.target===item.id || view.target?.endsWith('--'+item.id),`${type.shortName}.${item.name}`);
        assert.ok(details.includes(`id="${view.target}"`),`${type.shortName}.${item.name}`);
        continue;
      }
      assert.ok(content.includes(`id="${item.id}"`),`${categoryKey(topic)}: ${type.shortName}.${item.name}`);
      view.open(item.id);
      assert.equal(view.breadcrumb,`${topic.path[1]} / ${topic.path[3]}`);
      assert.equal(view.target,item.id);
    }
  }
});

test('legacy modification field anchors open the shared field rather than a duplicate card', () => {
  const view=page();
  const id=member('VmIdentityFields','Name').id;
  const content=view.open('modify-'+id);
  assert.equal(view.breadcrumb,'虚拟机 / 基本信息');
  assert.ok(content.includes(`id="${id}"`));
  assert.equal(view.target,'modify-'+id);
});

test('repeated category clicks use the same scroll position and preserve modified-click behavior', () => {
  const view=page();
  const topic=topics.find(c=>categoryKey(c)==='虚拟机/基本信息');
  view.open(topic.id);
  assert.equal(view.scrollY,0);
  for (let i=0;i<2;i++) {
    view.setScroll(400);
    assert.equal(view.clickCurrentLink(),true);
    assert.equal(view.scrollY,0);
  }
  for (const options of [{ctrlKey:true},{metaKey:true},{shiftKey:true},{altKey:true},{button:1},{defaultPrevented:true}]) {
    view.setScroll(400);
    assert.equal(view.clickCurrentLink(options),false);
    assert.equal(view.scrollY,400);
  }
});

test('method cards show declarations and exceptions as rows, and include notes in the description', () => {
  const view=page();
  const query=member('VmQueryService','QueryAsync');
  const content=view.open(query.id);
  const card=elementAt(content,query.id);
  assert.ok(card.includes('读取指定虚拟机的所选字段。'));
  assert.ok(card.includes('<dt>参数</dt>'));
  assert.ok(card.includes('<dt>返回</dt>'));
  assert.ok(stripLinks(card).includes(stripLinks(query.presentation.valueTypeHtml)));
  assert.ok(card.includes('<dt>声明</dt>'));
  assert.ok(stripLinks(card).includes(stripLinks(query.signatureHtml)));
  assert.ok(card.includes(query.presentation.descriptionHtml + query.presentation.notesHtml));
  assert.ok(card.includes('<dt>异常</dt>'));
  assert.ok(card.includes(query.presentation.exceptionsHtml));
  assert.doesNotMatch(card,/<details\b|<summary\b|<dt>类型<\/dt>|补充说明|完整声明/);
});

test('method headings use Chinese display names while code identifiers remain unchanged', () => {
  for (const {type,member:item} of members.filter(({member:item})=>item.presentation.callable)) {
    assert.match(item.displayName,/\p{Script=Han}/u,`${type.shortName}.${item.name}`);
  }
  const view=page();
  const method=member('VmBootService','GetBootOrderAsync');
  const content=view.open(method.id);
  const card=elementAt(content,method.id);
  assert.match(card,/<h3>读取启动顺序<\/h3>/);
  assert.ok(card.includes('VmBootService.GetBootOrderAsync'));
  assert.ok(stripLinks(card).includes(stripLinks(method.signatureHtml)));
  for (const topic of topics) {
    for (const card of view.open(topic.id).matchAll(/class="field-sheet member-card method-card"[^>]*><h\d>([^<]+)<\/h\d>/g))
      assert.match(card[1],/\p{Script=Han}/u,`untranslated method in ${categoryKey(topic)}`);
  }
});

test('method titles distinguish operations and overloads throughout the reference', () => {
  const issues = [];
  for (const type of data.types) {
    const titles = new Set();
    for (const item of type.members.filter(item => item.presentation.callable)) {
      if (titles.has(item.displayName)) issues.push(`${type.shortName}: ${item.displayName}`);
      titles.add(item.displayName);
    }
  }
  const view = page(), seen = new Set();
  for (const route of [...data.categories, ...data.types, ...members.map(item => item.member)]) {
    const content = view.open(route.id);
    if (seen.has(content)) continue;
    seen.add(content);
    const titles = new Set();
    for (const match of content.matchAll(/class="field-sheet member-card method-card"[^>]*><h\d>([^<]+)<\/h\d>/g)) {
      if (titles.has(match[1])) issues.push(`${view.breadcrumb}: ${match[1]}`);
      titles.add(match[1]);
    }
  }
  assert.deepEqual(issues, []);
});

test('capability descriptions are grouped under functionality notes without duplicate host notes', () => {
  const view = page();
  for (const usage of data.businessUsage) {
    const category = data.categories.find(item => item.path[0] === '业务层面' && item.path.length === 2 && item.path[1] === usage.category);
    const content = view.open(category.id);
    const section = elementAt(content, `${category.id}--description`);
    assert.match(section, />功能说明<\/h2>/);
    assert.ok(stripLinks(section).includes(stripLinks(usage.html)), usage.category);
    assert.ok(!stripLinks(content.replace(section, '')).includes(stripLinks(usage.html)), usage.category);
  }
  for (const topic of topics.filter(item => item.path[1] === '宿主')) {
    const usages = data.hostUsage.filter(item => item.category === topic.path[3]);
    if (!usages.length) continue;
    const content = view.open(topic.id);
    const sectionId = content.match(/class="capability-usage" id="([^"]+)"/)[1];
    const section = elementAt(content, sectionId);
    assert.match(section, />功能说明<\/h2>/);
    for (const usage of usages) {
      assert.equal(stripLinks(section).split(stripLinks(usage.html)).length - 1, 1, `${topic.path[3]}: ${usage.title}`);
      assert.ok(section.includes(`id="${usage.id}"`), 'preserve existing anchors');
    }
  }
});

test('processor validation references existing fields without repeating settings or capability enums', () => {
  const view=page();
  const validation=member('VmProcessorService','GetModificationCondition');
  const content=view.open(validation.id);
  const card=elementAt(content,validation.id);
  assert.doesNotMatch(card,/class="enum-values"/);
  for (const field of ['CanModify','Reason','ErrorKind','RequiredState']) assert.ok(card.includes(field));
  assert.doesNotMatch(card,/>检查处理器修改参数</);
  assert.ok(card.includes('VmProcessorSettings'));
  const capabilities=data.types.find(type=>type.shortName==='VmProcessorCapability');
  for(const capability of capabilities.members) {
    const targetPage=view.open(capability.id);
    assert.ok(view.target);
    const target=elementAt(targetPage,view.target);
    assert.ok(target.includes('VmProcessorCapability.'+capability.name));
    assert.ok(target.includes('VmProcessorFields.'));
  }
  for(const topic of topics) assert.doesNotMatch(view.open(topic.id),/>参数与结果</);
});

test('all public types and members still have a reachable documentation destination', () => {
  const view=page();
  for(const type of data.types) {
    let content=view.open(type.id);
    assert.doesNotMatch(content,/未找到文档/,type.shortName);
    if(view.target) assert.ok(content.includes(`id="${view.target}"`),type.shortName);
    for(const item of type.members) {
      content=view.open(item.id);
      assert.doesNotMatch(content,/未找到文档/,type.shortName+'.'+item.name);
      assert.ok(content.includes(`id="${view.target || item.id}"`),type.shortName+'.'+item.name);
    }
  }
});
