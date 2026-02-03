const statusEl = document.getElementById("connectionStatus");
const btnStart = document.getElementById("btnStart");
const btnStop = document.getElementById("btnStop");
const btnScreen = document.getElementById("btnScreen");
const btnMove = document.getElementById("btnMove");
const btnColor = document.getElementById("btnColor");
const btnRetry = document.getElementById("btnRetry");
const btnFollowup = document.getElementById("btnFollowup");
const btnClearMemory = document.getElementById("btnClearMemory");
const followupInput = document.getElementById("followupInput");
const memoryCountEl = document.getElementById("memoryCount");
const screenStage = document.getElementById("screenStage");
const screenImage = document.getElementById("screenImage");
const moveOverlay = document.getElementById("moveOverlay");
const recordingIndicator = document.getElementById("recordingIndicator");
const answerEl = document.getElementById("answer");
const errorPanel = document.getElementById("errorPanel");
const errorMessage = document.getElementById("errorMessage");
const statusMessage = document.getElementById("statusMessage");

let connection = null;
let isRecording = false;
let isConnected = false;
let moveMode = false;
let hasUserTransform = false;
let offsetX = 0;
let offsetY = 0;
let scale = 1;
const activePointers = new Map();
let panStart = null;
let pinchStart = null;
let streamToken = null;
let streamFps = 10;
let streamEnabled = true;
let streamTimer = null;
let streamPending = false;
let textInverted = false;
let lastAnswerRaw = "";
let shikiModulePromise = null;
if (typeof marked !== "undefined") {
  marked.setOptions({ langPrefix: "language-" });
}

function setConnectionStatus(text, ok) {
  isConnected = ok;
  statusEl.textContent = text;
  statusEl.dataset.state = ok ? "ok" : "bad";

  if (!ok) {
    btnStart.disabled = true;
    btnStop.disabled = true;
    btnScreen.disabled = true;
    btnFollowup.disabled = true;
    btnClearMemory.disabled = true;
    stopScreenStream();
  } else {
    btnFollowup.disabled = false;
    btnClearMemory.disabled = false;
    setRecording(isRecording);
    startScreenStream();
  }
}

function setRecording(value) {
  isRecording = value;
  recordingIndicator.classList.toggle("hidden", !value);
  if (!isConnected) {
    btnStart.disabled = true;
    btnStop.disabled = true;
    btnScreen.disabled = true;
    btnFollowup.disabled = true;
    btnClearMemory.disabled = true;
    return;
  }
  btnStart.disabled = value;
  btnStop.disabled = !value;
  btnScreen.disabled = value;
  btnFollowup.disabled = value;
  btnClearMemory.disabled = false;
}

function showError(message, retryable) {
  errorMessage.textContent = message;
  errorPanel.classList.remove("hidden");
  btnRetry.disabled = !retryable;
  btnRetry.style.display = retryable ? "inline-block" : "none";
}

function clearError() {
  errorPanel.classList.add("hidden");
  errorMessage.textContent = "";
}

function clearOutputs() {
  answerEl.innerHTML = "";
  lastAnswerRaw = "";
  statusMessage.textContent = "";
  clearError();
}

function updateMemory(count) {
  memoryCountEl.textContent = `Memory: ${count}`;
}

function renderMarkdown(text) {
  if (typeof marked !== "undefined" && typeof DOMPurify !== "undefined") {
    const html = marked.parse(text, { breaks: true });
    return DOMPurify.sanitize(html);
  }
  return (text || "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/\n/g, "<br>");
}

async function getShikiModule() {
  if (!shikiModulePromise) {
    shikiModulePromise = import("https://esm.sh/shiki@3.0.0");
  }
  return shikiModulePromise;
}

async function highlightCodeBlocks(container) {
  let shiki;
  try {
    shiki = await getShikiModule();
  } catch {
    return;
  }

  const blocks = container.querySelectorAll("pre code");
  if (!blocks.length || !shiki?.codeToHtml) {
    return;
  }

  const theme = document.body.classList.contains("text-invert")
    ? "light-plus"
    : "dark-plus";

  for (const block of blocks) {
    const raw = block.textContent || "";
    const langMatch = (block.className || "").match(/language-([\w-]+)/i);
    const lang = langMatch ? langMatch[1] : "text";
    let html = "";
    try {
      html = await shiki.codeToHtml(raw, { lang, theme });
    } catch {
      html = await shiki.codeToHtml(raw, { lang: "text", theme });
    }

    const wrapper = document.createElement("div");
    wrapper.innerHTML = html;
    const pre = wrapper.querySelector("pre");
    if (!pre) {
      continue;
    }
    pre.classList.add("shiki-block");
    pre.removeAttribute("style");
    block.parentElement.replaceWith(pre);
  }
}

async function renderAnswer(raw) {
  lastAnswerRaw = raw || "";
  answerEl.innerHTML = renderMarkdown(lastAnswerRaw);
  await highlightCodeBlocks(answerEl);
  if (typeof renderMathInElement === "function") {
    try {
      renderMathInElement(answerEl, {
        delimiters: [
          { left: "$$", right: "$$", display: true },
          { left: "$", right: "$", display: false }
        ],
        throwOnError: false,
        ignoredTags: ["script", "noscript", "style", "textarea", "pre", "code"]
      });
    } catch {
      // ignore math render errors
    }
  }
}

function applyTransform() {
  screenImage.style.transform = `translate(${offsetX}px, ${offsetY}px) scale(${scale})`;
}

function resetTransformToFit() {
  if (!screenImage.naturalWidth || !screenImage.naturalHeight) {
    return;
  }
  const rect = screenStage.getBoundingClientRect();
  const scaleX = rect.width / screenImage.naturalWidth;
  const scaleY = rect.height / screenImage.naturalHeight;
  scale = Math.max(scaleX, scaleY);
  const imageW = screenImage.naturalWidth * scale;
  const imageH = screenImage.naturalHeight * scale;
  offsetX = (rect.width - imageW) / 2;
  offsetY = (rect.height - imageH) / 2;
  applyTransform();
}

function setMoveMode(value) {
  moveMode = value;
  btnMove.textContent = value ? "Move: ON" : "Move: OFF";
  moveOverlay.classList.toggle("hidden", !value);
  document.body.classList.toggle("move-mode", value);
}

function setTextMode(inverted) {
  textInverted = inverted;
  document.body.classList.toggle("text-invert", inverted);
  btnColor.textContent = inverted ? "Theme: Light" : "Theme: Dark";
  try {
    localStorage.setItem("tp_text_invert", inverted ? "1" : "0");
  } catch {
    // ignore
  }
  if (lastAnswerRaw) {
    renderAnswer(lastAnswerRaw);
  }
}

function startScreenStream() {
  if (!streamEnabled || !streamToken || !isConnected) {
    return;
  }
  if (streamTimer) {
    return;
  }
  scheduleNextFrame();
  const interval = Math.max(1, streamFps);
  streamTimer = setInterval(() => {
    if (!streamPending) {
      scheduleNextFrame();
    }
  }, 1000 / interval);
}

function stopScreenStream() {
  if (streamTimer) {
    clearInterval(streamTimer);
    streamTimer = null;
  }
  streamPending = false;
}

function scheduleNextFrame() {
  if (!streamEnabled || !streamToken || !isConnected) {
    return;
  }
  streamPending = true;
  const url = `/stream.jpg?t=${encodeURIComponent(streamToken)}&_=${Date.now()}`;
  screenImage.src = url;
}

function requireToken() {
  const token = new URLSearchParams(window.location.search).get("t");
  if (!token) {
    showError("Token missing in URL. Scan the QR from the PC app.", false);
    btnStart.disabled = true;
    btnStop.disabled = true;
    btnScreen.disabled = true;
    btnFollowup.disabled = true;
    btnClearMemory.disabled = true;
    return null;
  }
  return token;
}

function initPinchState() {
  const points = Array.from(activePointers.values());
  if (points.length !== 2) {
    return;
  }
  const dx = points[0].x - points[1].x;
  const dy = points[0].y - points[1].y;
  const dist = Math.hypot(dx, dy);
  const center = { x: (points[0].x + points[1].x) / 2, y: (points[0].y + points[1].y) / 2 };
  pinchStart = { dist, scale, offsetX, offsetY, center };
}

async function startConnection(token) {
  connection = new signalR.HubConnectionBuilder()
    .withUrl(`/hub?t=${encodeURIComponent(token)}`)
    .withAutomaticReconnect()
    .build();

  connection.on("state", (data) => {
    setRecording(!!data.isRecording);
    if (data.hasKey === false) {
      showError("GEMINI_API_KEY is not set on the PC.", false);
    }
    streamEnabled = data.streamEnabled !== false;
    streamFps = data.streamFps || 10;
    startScreenStream();
  });

  connection.on("memory", (data) => {
    updateMemory(data.count ?? 0);
  });

  connection.on("recording", (data) => {
    setRecording(!!data.isRecording);
  });

  connection.on("answer", (data) => {
    const raw = data.text || "";
    renderAnswer(raw);
    statusMessage.textContent = "";
  });

  connection.on("error", (data) => {
    showError(data.message || "Unknown error", !!data.retryable);
  });

  connection.on("status", (data) => {
    statusMessage.textContent = data.message || "";
  });

  connection.onreconnecting(() => {
    setConnectionStatus("Reconnecting...", false);
  });

  connection.onreconnected(() => {
    setConnectionStatus("Connected", true);
  });

  connection.onclose(() => {
    setConnectionStatus("Disconnected", false);
  });

  try {
    await connection.start();
    setConnectionStatus("Connected", true);
  } catch (err) {
    setConnectionStatus("Disconnected", false);
    showError("Failed to connect to PC. Reload the page.", false);
  }
}

btnStart.addEventListener("click", async () => {
  if (!connection) return;
  clearOutputs();
  statusMessage.textContent = "Starting recording...";
  try {
    await connection.invoke("StartRecording");
  } catch (err) {
    showError("Failed to start recording.", false);
  }
});

btnStop.addEventListener("click", async () => {
  if (!connection) return;
  statusMessage.textContent = "Stopping and processing...";
  try {
    await connection.invoke("StopRecording");
  } catch (err) {
    showError("Failed to stop recording.", true);
  }
});

btnScreen.addEventListener("click", async () => {
  if (!connection) return;
  clearOutputs();
  statusMessage.textContent = "Capturing screenshot...";
  try {
    await connection.invoke("Screenshot");
  } catch (err) {
    showError("Failed to capture screenshot.", true);
  }
});

btnFollowup.addEventListener("click", async () => {
  if (!connection) return;
  const text = (followupInput.value || "").trim();
  if (!text) {
    showError("Please enter follow-up text.", false);
    return;
  }
  statusMessage.textContent = "Sending follow-up...";
  try {
    await connection.invoke("FollowUp", text);
    followupInput.value = "";
  } catch (err) {
    showError("Failed to send follow-up.", false);
  }
});

btnClearMemory.addEventListener("click", async () => {
  if (!connection) return;
  statusMessage.textContent = "Clearing memory...";
  try {
    await connection.invoke("ClearMemory");
    clearOutputs();
  } catch (err) {
    showError("Failed to clear memory.", false);
  }
});

btnMove.addEventListener("click", () => {
  setMoveMode(!moveMode);
});

btnColor.addEventListener("click", () => {
  setTextMode(!textInverted);
});

moveOverlay.addEventListener("pointerdown", (event) => {
  if (!moveMode) return;
  event.preventDefault();
  moveOverlay.setPointerCapture(event.pointerId);
  activePointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
  hasUserTransform = true;
  if (activePointers.size === 1) {
    panStart = { x: event.clientX, y: event.clientY, offsetX, offsetY };
    pinchStart = null;
  } else if (activePointers.size === 2) {
    initPinchState();
  }
});

moveOverlay.addEventListener("pointermove", (event) => {
  if (!moveMode || !activePointers.has(event.pointerId)) return;
  event.preventDefault();
  activePointers.set(event.pointerId, { x: event.clientX, y: event.clientY });

  if (activePointers.size === 1 && panStart) {
    offsetX = panStart.offsetX + (event.clientX - panStart.x);
    offsetY = panStart.offsetY + (event.clientY - panStart.y);
    applyTransform();
    return;
  }

  if (activePointers.size === 2) {
    if (!pinchStart) {
      initPinchState();
    }
    if (!pinchStart) return;
    const points = Array.from(activePointers.values());
    const dx = points[0].x - points[1].x;
    const dy = points[0].y - points[1].y;
    const dist = Math.hypot(dx, dy);
    const center = { x: (points[0].x + points[1].x) / 2, y: (points[0].y + points[1].y) / 2 };
    const newScale = Math.min(5, Math.max(0.2, pinchStart.scale * (dist / pinchStart.dist)));
    const imgX = (center.x - pinchStart.offsetX) / pinchStart.scale;
    const imgY = (center.y - pinchStart.offsetY) / pinchStart.scale;
    scale = newScale;
    offsetX = center.x - imgX * scale;
    offsetY = center.y - imgY * scale;
    applyTransform();
  }
});

moveOverlay.addEventListener("pointerup", (event) => {
  if (!activePointers.has(event.pointerId)) return;
  activePointers.delete(event.pointerId);
  if (activePointers.size < 2) {
    pinchStart = null;
  }
  if (activePointers.size === 0) {
    panStart = null;
  }
});

moveOverlay.addEventListener("pointercancel", (event) => {
  if (!activePointers.has(event.pointerId)) return;
  activePointers.delete(event.pointerId);
  if (activePointers.size < 2) {
    pinchStart = null;
  }
  if (activePointers.size === 0) {
    panStart = null;
  }
});

screenImage.addEventListener("load", () => {
  streamPending = false;
  if (!hasUserTransform) {
    resetTransformToFit();
  }
});

screenImage.addEventListener("error", () => {
  streamPending = false;
});

window.addEventListener("resize", () => {
  if (!hasUserTransform) {
    resetTransformToFit();
  }
});

btnRetry.addEventListener("click", async () => {
  if (!connection) return;
  statusMessage.textContent = "Retrying...";
  clearError();
  try {
    await connection.invoke("Retry");
  } catch (err) {
    showError("Retry failed.", true);
  }
});

setConnectionStatus("Disconnected", false);
updateMemory(0);
setMoveMode(false);
try {
  const saved = localStorage.getItem("tp_text_invert");
  setTextMode(saved === "1");
} catch {
  setTextMode(false);
}

const token = requireToken();
if (token) {
  streamToken = token;
  startConnection(token);
}
