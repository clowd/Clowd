import { Tldraw, TLUiOverrides } from "tldraw";
import { getImageUri } from "./ipc";
import 'tldraw/tldraw.css';

interface CanvasProps {
    width?: number;
    height?: number;
    imagePath?: string;
}

const myOverrides: TLUiOverrides = {
    actions(editor, actions) {
        // You can delete actions, but remember to
        // also delete the menu items that reference them!
        delete actions['insert-embed']

        // Create a new action or replace an existing one
        actions['my-new-action'] = {
            id: 'my-new-action',
            label: 'My new action',
            readonlyOk: true,
            kbd: '$u',
            onSelect(source: any) {
                // Whatever you want to happen when the action is run
                window.alert('My new action just happened!')
            },
        }
        return actions
    },
}

export const Canvas: React.FC<CanvasProps> = ({ width, height, imagePath }) => {
    return (
        <div style={{ position: 'fixed', inset: 0 }}>
            <Tldraw
                inferDarkMode={true}
                autoFocus={true}
                overrides={myOverrides}
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