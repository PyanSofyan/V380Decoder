using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace V380Decoder.src
{
    public class WebPage
    {
        public static string GetHtml()
        {
            return @"
            <!DOCTYPE html>
            <html>

            <head>
                <title>V380 Control Panel</title>
                <meta name='viewport' content='width=device-width, initial-scale=1'>
                <style>
                    * {
                        margin: 0;
                        padding: 0;
                        box-sizing: border-box;
                    }

                    body {
                        font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Arial, sans-serif;
                        background: linear-gradient(135deg, #ABDCFF 0%, #0396FF 100%);
                        min-height: 100vh;
                        display: flex;
                        align-items: center;
                        justify-content: center;
                        padding: 20px;
                    }

                    .container {
                        background: white;
                        border-radius: 20px;
                        padding: 30px;
                        box-shadow: 0 20px 60px rgba(0, 0, 0, 0.3);
                        max-width: 500px;
                        width: 100%;
                    }

                    h1 {
                        text-align: center;
                        color: #333;
                        margin-bottom: 30px;
                        font-size: 28px;
                    }

                    .section {
                        margin-bottom: 30px;
                    }

                    .section-title {
                        font-size: 16px;
                        font-weight: 600;
                        color: #666;
                        margin-bottom: 15px;
                        text-transform: uppercase;
                        letter-spacing: 1px;
                    }

                    .ptz-grid {
                        display: grid;
                        grid-template-columns: repeat(3, 1fr);
                        gap: 10px;
                        margin-bottom: 10px;
                    }

                    .btn {
                        padding: 15px;
                        border: none;
                        border-radius: 10px;
                        font-size: 16px;
                        font-weight: 600;
                        cursor: pointer;
                        transition: all 0.2s;
                        background: #0396FF;
                        color: white;
                        box-shadow: 0 4px 15px rgb(3, 150, 255, 0.4);
                    }

                    .btn:hover {
                        transform: translateY(-2px);
                        box-shadow: 0 6px 20px rgb(3, 150, 255, 0.6);
                    }

                    .btn:active {
                        transform: translateY(0);
                    }

                    .btn-group-light {
                        display: grid;
                        grid-template-columns: repeat(3, 1fr);
                        gap: 10px;
                    }

                    .btn-group-image {
                        display: grid;
                        grid-template-columns: repeat(4, 1fr);
                        gap: 10px;
                    }

                    .btn-secondary {
                        background: #48bb78;
                        box-shadow: 0 4px 15px rgba(72, 187, 120, 0.4);
                    }

                    .btn-secondary:hover {
                        box-shadow: 0 6px 20px rgba(72, 187, 120, 0.6);
                    }

                    .btn-tertiary {
                        background: #ed8936;
                        box-shadow: 0 4px 15px rgba(237, 137, 54, 0.4);
                    }

                    .btn-tertiary:hover {
                        box-shadow: 0 6px 20px rgba(237, 137, 54, 0.6);
                    }

                    .status {
                        margin-top: 20px;
                        padding: 15px;
                        border-radius: 10px;
                        background: #f7fafc;
                        text-align: center;
                        font-size: 14px;
                        color: #666;
                        display: none;
                    }

                    .status.show {
                        display: block;
                    }

                    .status.success {
                        background: #c6f6d5;
                        color: #22543d;
                    }

                    .status.error {
                        background: #fed7d7;
                        color: #742a2a;
                    }

                    .empty {
                        grid-column: 2;
                    }
                </style>
            </head>

            <body>
                <div class='container'>
                    <h1>V380 Control</h1>

                    <div class='section'>
                        <div class='section-title'>PTZ Control</div>
                        <div class='ptz-grid'>
                            <div></div>
                            <button class='btn' onclick='cmd(""/api/ptz/up"")'>UP</button>
                            <div></div>
                            <button class='btn' onclick='cmd(""/api/ptz/left"")'>LEFT</button>
                            <button class='btn'></button>
                            <button class='btn' onclick='cmd(""/api/ptz/right"")'>RIGHT</button>
                            <div></div>
                            <button class='btn' onclick='cmd(""/api/ptz/down"")'>DOWN</button>
                            <div></div>
                        </div>
                    </div>

                    <div class='section'>
                        <div class='section-title'>Light Control</div>
                        <div class='btn-group-light'>
                            <button class='btn btn-secondary' onclick='cmd(""/api/light/on"")'>ON</button>
                            <button class='btn btn-secondary' onclick='cmd(""/api/light/off"")'>OFF</button>
                            <button class='btn btn-secondary' onclick='cmd(""/api/light/auto"")'>AUTO</button>
                        </div>
                    </div>

                    <div class='section'>
                        <div class='section-title'>Image Mode</div>
                        <div class='btn-group-image'>
                            <button class='btn btn-tertiary' onclick='cmd(""/api/image/color"")'>COLOR</button>
                            <button class='btn btn-tertiary' onclick='cmd(""/api/image/bw"")'>B&W</button>
                            <button class='btn btn-tertiary' onclick='cmd(""/api/image/auto"")'>AUTO</button>
                            <button class='btn btn-tertiary' onclick='cmd(""/api/image/flip"")'>FLIP</button>
                        </div>
                    </div>

                </div>

                <script>
                    async function cmd(url) {
                        const status = document.getElementById('status');
                        const res = await fetch(url, { method: 'POST' });
                        const data = await res.json();
                        console.log(data);
                    }
                </script>
            </body>

            </html>";
        }
    }
}