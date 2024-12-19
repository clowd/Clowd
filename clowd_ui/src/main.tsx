import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";

console.log("Hello from main.tsx");
console.log(window.location.pathname);
console.log(window.location);
console.log(window.location.search);

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
