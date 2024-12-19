import { Tldraw } from "tldraw";
import { getImageUri } from "./ipc";
import 'tldraw/tldraw.css';

interface CanvasProps {
    width?: number;
    height?: number;
    imagePath?: string;
}

export const Canvas: React.FC<CanvasProps> = ({ width, height, imagePath }) => {
    return (
        <div style={{ position: 'fixed', inset: 0 }}>
            <Tldraw
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