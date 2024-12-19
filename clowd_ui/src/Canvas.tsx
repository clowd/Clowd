import { DefaultMainMenu, DefaultMainMenuContent, DefaultNavigationPanel, DefaultQuickActions, DefaultQuickActionsContent, DefaultZoomMenu, DefaultZoomMenuContent, TLComponents, Tldraw, TldrawUiMenuActionItem, TldrawUiMenuContextProvider, TldrawUiMenuGroup, TldrawUiMenuItem, TLUiOverrides, useCanRedo, useCanUndo, useTldrawUiComponents } from "tldraw";
import { getImageUri } from "./ipc";
import 'tldraw/tldraw.css';
import { CustomZoomMenu } from "./components/CustomZoom";

// https://github.com/tldraw/tldraw/blob/0d88f3e0a5efee2e23b10c56cdadc3fcd976b984/apps/examples/src/examples/custom-menus/CustomMenusExample.tsx#L181

function CustomMainMenu() {
    return (
        <>
            <DefaultMainMenu>
                <div style={{ backgroundColor: 'thistle' }}>
                    <TldrawUiMenuGroup id="example">
                        <TldrawUiMenuItem
                            id="like"
                            label="Like my posts"
                            icon="external-link"
                            readonlyOk
                            onSelect={() => {
                                window.open('https://x.com/tldraw', '_blank')
                            }}
                        />
                    </TldrawUiMenuGroup>
                </div>
                <DefaultMainMenuContent />
            </DefaultMainMenu>
            <DefaultQuickActions>
                <DefaultQuickActionsContent />
                {/* <div style={{ backgroundColor: 'thistle' }}>
                    <TldrawUiMenuItem id="code" icon="code" onSelect={() => window.alert('code')} />
                </div> */}
            </DefaultQuickActions>
            <CustomZoomMenu />
            {/* <DefaultZoomMenu>
                <DefaultZoomMenuContent />
            </DefaultZoomMenu> */}
            {/* <ZoomMenu /> */}
        </>
    )
}

function CustomQuickActions() {
    const canUndo = useCanUndo()
    const canRedo = useCanRedo()
    // return (
    //     <DefaultQuickActions>
    //         <DefaultQuickActionsContent />
    //         <div style={{ backgroundColor: 'thistle' }}>
    //             <TldrawUiMenuItem id="code" icon="code" onSelect={() => window.alert('code')} />
    //         </div>
    //     </DefaultQuickActions>
    // )
    return <>

        <TldrawUiMenuContextProvider type="small-icons" sourceId="quick-actions">
            <TldrawUiMenuActionItem actionId="undo" disabled={!canUndo} />
            <TldrawUiMenuActionItem actionId="redo" disabled={!canRedo} />
        </TldrawUiMenuContextProvider>

    </>
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
    MainMenu: CustomMainMenu,
    PageMenu: null,
    QuickActions: null,
    ActionsMenu: null,
    NavigationPanel: null,
    // ZoomMenu: null,
    // Minimap: null,
    // StylePanel: CustomStylePanel,
    // Toolbar: CustomToolbar,
    // ZoomMenu: CustomZoomMenu,
}

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

export const Canvas: React.FC<CanvasProps> = ({ width, height, imagePath }) => {
    return (
        <div style={{ position: 'fixed', inset: 0 }}>
            <Tldraw
                inferDarkMode={true}
                autoFocus={true}
                components={components}
                cameraOptions={{ wheelBehavior: 'zoom' }}
                onMount={(editor) => {
                    if (!!imagePath) {
                        getImageUri(imagePath).then((imageUrl) => {
                            editor.run(
                                () => {
                                    let imageShape = {
                                        id: "initial-image" as any,
                                        type: 'image',
                                        props: {
                                            x: 0,
                                            y: 0,
                                            width: width,
                                            height: height,
                                            url: imageUrl,
                                        },
                                    };
                                    editor.createShape(imageShape);
                                    editor.zoomToFit()
                                    editor.selectNone()
                                },
                                { history: 'ignore' }
                            );
                        });
                    }
                }}
            />
        </div>
    )
}

export default Canvas;