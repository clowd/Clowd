import {
  createShapeId,
  DefaultMainMenu,
  DefaultMainMenuContent,
  DefaultQuickActions,
  DefaultQuickActionsContent,
  TLComponents,
  Tldraw,
  TldrawOptions,
  TldrawUiButton,
  TldrawUiMenuActionItem,
  TldrawUiMenuContextProvider,
  TldrawUiMenuGroup,
  TldrawUiMenuItem,
  useCanRedo,
  useCanUndo,
  useDefaultColorTheme,
} from "tldraw";
import { getImageUri } from "./ipc";
import "tldraw/tldraw.css";
import { CustomZoomMenu } from "./components/CustomZoom";
import saveThin from "./assets/save-thin.svg";

// https://github.com/tldraw/tldraw/blob/main/apps/examples/src/examples/custom-menus/CustomMenusExample.tsx

// function CustomMainMenu() {
//   let theme = useDefaultColorTheme();
//   let isDark = theme.id === "dark";
//   let svgStyle = isDark ? { filter: "invert(1)" } : {};
//   return (
//     <>
//       <DefaultMainMenu>
//         <div style={{ backgroundColor: "thistle" }}>
//           <TldrawUiMenuGroup id="example">
//             <TldrawUiMenuItem
//               id="like"
//               label="Like my posts"
//               icon="external-link"
//               readonlyOk
//               onSelect={() => {
//                 window.open("https://x.com/tldraw", "_blank");
//               }}
//             />
//           </TldrawUiMenuGroup>
//         </div>
//         <DefaultMainMenuContent />
//       </DefaultMainMenu>
//       <DefaultQuickActions>
//         <DefaultQuickActionsContent />
//       </DefaultQuickActions>
//       <TldrawUiMenuContextProvider type="small-icons" sourceId="quick-actions">
//         <TldrawUiMenuItem
//           id="like"
//           label="Like my posts"
//           icon="clipboard-copy"
//           readonlyOk
//           onSelect={() => {
//             window.open("https://x.com/tldraw", "_blank");
//           }}
//         />
//         <TldrawUiButton type="icon" title={"Save"}>
//           <img
//             src={saveThin}
//             className="logo react"
//             alt="React logo"
//             width={18}
//             style={svgStyle}
//           />
//         </TldrawUiButton>
//       </TldrawUiMenuContextProvider>

//       <CustomZoomMenu />
//     </>
//   );
// }

function CustomQuickActions() {
  let theme = useDefaultColorTheme();
  let isDark = theme.id === "dark";
  let svgStyle = isDark ? { filter: "invert(1)" } : {};

  return (
    <DefaultQuickActions>
      <DefaultQuickActionsContent />
      <TldrawUiMenuItem
        id="clipboard-copy"
        label="Copy to clipboard"
        icon="clipboard-copy"
        readonlyOk
        onSelect={() => {
          window.open("https://x.com/tldraw", "_blank");
        }}
      />
      <TldrawUiButton type="icon" title={"Save"}>
        <img
          src={saveThin}
          className="logo react"
          alt="React logo"
          width={18}
          style={svgStyle}
        />
      </TldrawUiButton>
      <CustomZoomMenu />
    </DefaultQuickActions>
  );
}

const components: TLComponents = {
  // ActionsMenu: CustomActionsMenu,
  // ContextMenu: CustomContextMenu,
  // DebugMenu: CustomDebugMenu,
  // HelpMenu: CustomHelpMenu,
  // KeyboardShortcutsDialog: CustomKeyboardShortcutsDialog,
  // MainMenu: CustomMainMenu,
  // NavigationPanel: CustomNavigationPanel,
  // PageMenu: CustomPageMenu,
  // MainMenu: CustomMainMenu,
  PageMenu: null,
  QuickActions: CustomQuickActions,
  ActionsMenu: null,
  NavigationPanel: null,
  // ZoomMenu: null,
  // Minimap: null,
  // StylePanel: CustomStylePanel,
  // Toolbar: CustomToolbar,
  // ZoomMenu: CustomZoomMenu,
};

interface CanvasProps {
  width?: number;
  height?: number;
  imagePath?: string;
}

// const myOverrides: TLUiOverrides = {
//     actions(editor, actions) {
//         // You can delete actions, but remember to
//         // also delete the menu items that reference them!
//         delete actions['insert-embed']

//         // Create a new action or replace an existing one
//         actions['my-new-action'] = {
//             id: 'my-new-action',
//             label: 'My new action',
//             readonlyOk: true,
//             kbd: '$u',
//             onSelect(source: any) {
//                 // Whatever you want to happen when the action is run
//                 window.alert('My new action just happened!')
//             },
//         }
//         return actions
//     },
// }

const options: Partial<TldrawOptions> = {
  maxPages: 1, // disable pages
  actionShortcutsLocation: "menu", // move action shortcuts to the menu
};

export const Canvas: React.FC<CanvasProps> = ({ width, height, imagePath }) => {
  return (
    <div style={{ position: "fixed", inset: 0 }}>
      <Tldraw
        forceMobile={true}
        inferDarkMode={true}
        autoFocus={true}
        options={options}
        components={components}
        cameraOptions={{ wheelBehavior: "zoom" }}
        onMount={(editor) => {
          if (!!imagePath) {
            getImageUri(imagePath).then((imageUrl) => {
              editor.run(
                () => {
                  let imageShape = {
                    id: createShapeId(),
                    type: "image",
                    props: {
                      x: 0,
                      y: 0,
                      width: width,
                      height: height,
                      url: imageUrl,
                    },
                  };
                  editor.createShape(imageShape);
                  editor.zoomToFit();
                  editor.selectNone();
                },
                { history: "ignore" }
              );
            });
          }
        }}
      />
    </div>
  );
};

export default Canvas;
