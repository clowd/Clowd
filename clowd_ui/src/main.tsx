import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import Canvas from "./Canvas";
import queryString from 'query-string';
import { showCurrentWindow } from "./ipc";

import "./main.css";

setTimeout(() => {
  showCurrentWindow();
}, 100);

// import { Window } from "@tauri-apps/api/window";
// Window.getCurrent().close();
// console.log("Hello from main.tsx");
// console.log(window.location.pathname);
// console.log(window.location);
// console.log(window.location.search);

let RootComponent;
if (window.location.pathname === "/index.html/canvas") {
  RootComponent = Canvas;
} else {
  RootComponent = App;
}

const args = queryString.parse(location.search);

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    <div style={{ position: 'fixed', inset: 0 }}>
      <RootComponent {...args} />
    </div>
  </React.StrictMode>,
);
