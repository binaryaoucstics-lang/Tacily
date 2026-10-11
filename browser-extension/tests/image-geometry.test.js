"use strict";
const test = require("node:test"), assert = require("node:assert/strict");
const { imageGeometry, intersectImageRects, describeImageElement } = require("../image-geometry.js");

test("contain preserves image pixels with horizontal letterboxing", () => {
  assert.deepEqual(imageGeometry({ left: 20, top: 30, width: 800, height: 600 }, { width: 1000, height: 2000 }, "contain"),
    { left: 270, top: 30, width: 300, height: 600 });
});
test("cover and object-position keep the cropped source offset", () => {
  const result = imageGeometry({ left: 100, top: 200, width: 300, height: 300 }, { width: 1000, height: 500 }, "cover", "100% 50%");
  assert.deepEqual(result, { left: -200, top: 200, width: 600, height: 300 });
  assert.deepEqual(intersectImageRects(result, { left: 100, top: 200, width: 300, height: 300 }),
    { left: 100, top: 200, width: 300, height: 300 });
});
test("scroll clipping excludes offscreen source text", () => {
  assert.deepEqual(intersectImageRects({ left: 20, top: -200, width: 400, height: 900 }, { left: 0, top: 0, width: 800, height: 600 }),
    { left: 20, top: 0, width: 400, height: 600 });
});
test("none and scale-down preserve small images and pixel positions", () => {
  const box = { left: 10, top: 20, width: 400, height: 400 }, image = { width: 100, height: 200 };
  assert.deepEqual(imageGeometry(box, image, "none", "10px 20px"), { left: 20, top: 40, width: 100, height: 200 });
  assert.deepEqual(imageGeometry(box, image, "scale-down"), { left: 160, top: 120, width: 100, height: 200 });
  assert.deepEqual(imageGeometry(box, image, "none", "left 20px top 30px"), { left: 30, top: 50, width: 100, height: 200 });
});

test("object-position supports single vertical keywords, edge offsets and computed calc", () => {
  const box = { left: 10, top: 20, width: 400, height: 400 }, image = { width: 100, height: 200 };
  assert.deepEqual(imageGeometry(box, image, "none", "top"), { left: 160, top: 20, width: 100, height: 200 });
  assert.deepEqual(imageGeometry(box, image, "none", "right 20px bottom 30px"), { left: 290, top: 190, width: 100, height: 200 });
  assert.deepEqual(imageGeometry(box, image, "none", "center top 30px"), { left: 160, top: 50, width: 100, height: 200 });
  assert.deepEqual(imageGeometry(box, image, "none", "right 20px center"), { left: 290, top: 120, width: 100, height: 200 });
  assert.deepEqual(imageGeometry(box, image, "none", "calc(100% - 20px) calc(50% + 10px)"), { left: 290, top: 130, width: 100, height: 200 });
  assert.throws(() => imageGeometry(box, image, "none", "right wrong bottom 30px"));
});

function domFixture(imageOptions = {}, ancestors = []) {
  const standard = { width: "100px", height: "100px", boxSizing: "content-box", paddingLeft: "0px", paddingRight: "0px",
    paddingTop: "0px", paddingBottom: "0px", borderLeftWidth: "0px", borderRightWidth: "0px", borderTopWidth: "0px",
    borderBottomWidth: "0px", objectFit: "fill", objectPosition: "50% 50%", transform: "none", rotate: "none", scale: "none",
    translate: "none", perspective: "none", zoom: "1", visibility: "visible", opacity: "1", overflowX: "visible", overflowY: "visible" };
  const make = options => ({ clientLeft: 0, clientTop: 0, offsetWidth: 100, offsetHeight: 100, clientWidth: 100, clientHeight: 100,
    style: { ...standard, ...options.style }, parentElement: null, rect: { left: 10, top: 20, width: 100, height: 100, ...options.rect },
    ...options, getBoundingClientRect() { return this.rect; } });
  const image = make({ complete: true, isConnected: true, naturalWidth: 100, naturalHeight: 100, ...imageOptions });
  const parents = ancestors.map(make); let previous = image;
  for (const parent of parents) { previous.parentElement = parent; previous = parent; }
  const env = { innerWidth: 800, innerHeight: 600, devicePixelRatio: 1.5, document: {}, getComputedStyle: element => element.style,
    DOMMatrixReadOnly: class { constructor(value) {
      const [a, b, c, d] = value.match(/[-.\d]+/g).map(Number); Object.assign(this, { is2D: true, a, b, c, d });
    } } };
  return { image, parents, env };
}
const near = (actual, expected) => assert.ok(Math.abs(actual - expected) < 0.00001, `${actual} differs from ${expected}`);
test("actual X tweetPhoto layout uses the sibling background while retaining the transparent img as its source",()=>{
  const src="https://pbs.twimg.com/media/HUR9ZPUXEAAUBNd?format=jpg&name=900x900";
  const f=domFixture({src,currentSrc:src,style:{opacity:"0"},naturalWidth:900,naturalHeight:600},[{style:{width:"600px",height:"400px"},rect:{left:20,top:30,width:600,height:400},clientWidth:600,clientHeight:400}]);
  const layer={...f.parents[0],style:{...f.parents[0].style,backgroundImage:`url("${src}")`,backgroundSize:"contain",backgroundPosition:"50% 50%"},parentElement:f.parents[0]};
  f.parents[0].children=[layer,f.image];f.env.document.location={href:"https://x.com/example/status/1"};
  assert.deepEqual(describeImageElement(f.image,f.env).display,{left:20,top:30,width:600,height:400});
  layer.style.backgroundImage='url("https://pbs.twimg.com/another-image")';assert.throws(()=>describeImageElement(f.image,f.env),/unavailable/);
});
test("X transparent original maps to its visible exact-source background layer and retains contain letterboxing",()=>{
  const src="https://pbs.twimg.com/media/example.jpg?format=jpg&name=large";
  const f=domFixture({src,currentSrc:src,naturalWidth:400,naturalHeight:800,style:{opacity:"0"},rect:{left:0,top:0,width:1,height:1}},
    [{style:{width:"600px",height:"600px",backgroundImage:`url("${src}")`,backgroundSize:"contain",backgroundPosition:"50% 50%"},rect:{left:50,top:0,width:600,height:600},clientWidth:600,clientHeight:600}]);
  f.env.document.location={href:"https://x.com/example/status/1/photo/1"};
  const result=describeImageElement(f.image,f.env);assert.deepEqual(result.display,{left:200,top:0,width:300,height:600});assert.deepEqual(result.natural,{width:400,height:800});
  for(const change of [()=>f.parents[0].style.backgroundImage='url("https://other.invalid/photo.jpg")',()=>f.env.document.location.href="https://reader.example",()=>f.parents[0].style.opacity="0"]){
    f.parents[0].style.backgroundImage=`url("${src}")`;f.env.document.location.href="https://x.com/home";f.parents[0].style.opacity="1";change();assert.throws(()=>describeImageElement(f.image,f.env),/unavailable/);
  }
});
test("X background cover retains cropping and cannot expose a hidden ancestor",()=>{
  const src="https://pbs.twimg.com/media/test.png",f=domFixture({src,currentSrc:src,naturalWidth:800,naturalHeight:400,style:{opacity:"0"}},
    [{style:{width:"300px",height:"300px",backgroundImage:`url(${src})`,backgroundSize:"cover",backgroundPosition:"100% 50%"},rect:{left:100,top:100,width:300,height:300},clientWidth:300,clientHeight:300},
    {style:{visibility:"visible"}}]);f.env.document.location={href:"https://x.com/home"};
  const result=describeImageElement(f.image,f.env);assert.deepEqual(result.display,{left:-200,top:100,width:600,height:300});assert.deepEqual(result.clip,{left:100,top:100,width:300,height:300});
  f.parents[1].style.visibility="hidden";assert.throws(()=>describeImageElement(f.image,f.env),/unavailable/);
});

test("float content dimensions and borders do not inherit rounded offset/client drift", () => {
  const f = domFixture({ style: { width: "400.375px", height: "300.625px", paddingLeft: "3.25px", paddingRight: "5.5px",
    paddingTop: "2.125px", paddingBottom: "4.25px", borderLeftWidth: "1.25px", borderRightWidth: "1.75px",
    borderTopWidth: "1.5px", borderBottomWidth: "2px" }, rect: { left: 10.125, top: 20.5, width: 412.125, height: 310.5 },
    clientWidth: 409, clientHeight: 307, offsetWidth: 412, offsetHeight: 311, clientLeft: 1, clientTop: 2 });
  const shape = describeImageElement(f.image, f.env);
  near(shape.display.left, 14.625); near(shape.display.top, 24.125);
  near(shape.display.width, 400.375); near(shape.display.height, 300.625);
});

test("object-fit none and scale-down are resolved before an ancestor scale", () => {
  for (const fit of ["none", "scale-down"]) {
    const f = domFixture({ naturalWidth: 100, naturalHeight: 200, style: { width: "400px", height: "400px", objectFit: fit },
      rect: { left: 20, top: 40, width: 800, height: 800 }, offsetWidth: 400, offsetHeight: 400, clientWidth: 400, clientHeight: 400 },
    [{ style: { transform: "matrix(2, 0, 0, 2, 0, 0)" } }]);
    const shape = describeImageElement(f.image, f.env);
    assert.deepEqual(shape.display, { left: 320, top: 240, width: 200, height: 400 });
  }
});

test("nested overflow clips inside borders and excludes scrollbar area", () => {
  const f = domFixture({ style: { width: "400px", height: "400px" }, rect: { left: 0, top: 0, width: 400, height: 400 } },
    [{ style: { width: "220px", height: "180px", boxSizing: "border-box", borderLeftWidth: "2px", borderRightWidth: "2px",
      borderTopWidth: "3px", borderBottomWidth: "3px", overflowX: "auto", overflowY: "auto" },
    rect: { left: 40, top: 60, width: 220, height: 180 }, offsetWidth: 220, offsetHeight: 180, clientWidth: 201, clientHeight: 159,
    clientLeft: 2, clientTop: 3 }]);
  assert.deepEqual(describeImageElement(f.image, f.env).clip, { left: 42, top: 63, width: 201, height: 159 });
  f.parents[0].clientLeft = 17;
  assert.deepEqual(describeImageElement(f.image, f.env).clip, { left: 57, top: 63, width: 201, height: 159 });
});

test("content-box computed dimensions that already exclude fractional scrollbars are not mistaken for scale", () => {
  const f = domFixture({ style: { width: "500px", height: "500px" }, rect: { left: 0, top: 0, width: 500, height: 500 } },
    [{ style: { width: "354.667px", height: "314.667px", boxSizing: "content-box", paddingLeft: "13px", paddingRight: "13px",
      paddingTop: "13px", paddingBottom: "13px", borderLeftWidth: "8.66667px", borderRightWidth: "8.66667px",
      borderTopWidth: "8.66667px", borderBottomWidth: "8.66667px", overflowX: "auto", overflowY: "auto" },
    rect: { left: 48, top: 80, width: 413.3333435058594, height: 373.3333435058594 }, offsetWidth: 413,
    offsetHeight: 373, clientWidth: 381, clientHeight: 341, clientLeft: 9, clientTop: 9 }]);
  const clip = describeImageElement(f.image, f.env).clip;
  near(clip.left, 56.66667); near(clip.top, 88.66667); near(clip.width, 380.667); near(clip.height, 340.667);
});

test("shadow-host ancestry and CSS individual transforms are checked", () => {
  const f = domFixture(); const host = { style: { ...f.image.style, rotate: "15deg" } };
  f.image.getRootNode = () => ({ host });
  assert.throws(() => describeImageElement(f.image, f.env), /Unsupported transform/);
  host.style.rotate = "none"; host.style.scale = "-1 1";
  assert.throws(() => describeImageElement(f.image, f.env), /Unsupported transform/);
  host.style.scale = "2 2"; host.style.transform = "matrix(1, 0, 0.2, 1, 0, 0)";
  assert.throws(() => describeImageElement(f.image, f.env), /Unsupported transform/);
});

test("paint containment clips and transformed document roots fail safely", () => {
  const f = domFixture({ style: { width: "400px", height: "400px" }, rect: { left: 0, top: 0, width: 400, height: 400 } },
    [{ style: { contain: "paint" }, rect: { left: 40, top: 60, width: 100, height: 100 } }]);
  assert.deepEqual(describeImageElement(f.image, f.env).clip, { left: 40, top: 60, width: 100, height: 100 });
  f.env.document.documentElement = f.parents[0]; f.parents[0].style.translate = "10px";
  assert.throws(() => describeImageElement(f.image, f.env), /Unsupported transform/);
});

test("shared frame cache reads common ancestor style once without stale cross-frame reuse", () => {
  const f = domFixture({}, [{}]), other = { ...f.image, parentElement: f.parents[0] }, counts = new Map();
  f.env.getComputedStyle = element => { counts.set(element, (counts.get(element) || 0) + 1); return element.style; };
  const cache = new Map(); describeImageElement(f.image, f.env, cache); describeImageElement(other, f.env, cache);
  assert.equal(counts.get(f.parents[0]), 1);
  describeImageElement(f.image, f.env, new Map()); assert.equal(counts.get(f.parents[0]), 2);
});
