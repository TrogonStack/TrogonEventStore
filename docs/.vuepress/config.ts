import {defineUserConfig} from "vuepress";
import containers from "./lib/containers";
import {importCodePlugin} from "./markdown/xode/importCodePlugin";
import {resolveMultiSamplesPath} from "./lib/samples";
import {linkCheckPlugin} from "./markdown/linkCheck";
import {replaceLinkPlugin} from "./markdown/replaceLink";
import viteBundler from "@vuepress/bundler-vite";
import {defaultTheme} from "@vuepress/theme-default";
import {containerPlugin} from "@vuepress/plugin-container";

export default defineUserConfig({
    title: "EventStoreDB Documentation",
    description: "The stream database built for Event Sourcing",
    bundler: viteBundler(),
    markdown: {importCode: false},
    extendsMarkdown: md => {
        md.use(importCodePlugin, {
            handleImportPath: s => resolveMultiSamplesPath(s)
        });
        md.use(linkCheckPlugin);
        md.use(replaceLinkPlugin, {
            replaceLink: (link: string, _) => link
                .replace("@server", "")
        });
    },
    theme: defaultTheme({
        sidebarDepth: 2,
        docsDir: ".",
        sidebar: {
            "/": require("../sidebar")
        },
        navbar: [
            {
                text: "Server",
                link: "/",
            }
        ]
    }),
    plugins: [
        containers("tabs", "TabView", type => `${type ? ` type='${type}'` : ""}`),
        containers("tab", "TabPanel", label => `header="${label}"`),
        containerPlugin( {
            type: "note",
            before: title => `<div class="custom-container note"><p class="custom-container-title">${title === "" ? "NOTE" : title}</p>`,
            after: _ => `</div>`
        }),
        containerPlugin ({
            type: "card",
            before: _ => `<Card><template #content>`,
            after: _ => `</template></Card>`
        }),
    ],
});
