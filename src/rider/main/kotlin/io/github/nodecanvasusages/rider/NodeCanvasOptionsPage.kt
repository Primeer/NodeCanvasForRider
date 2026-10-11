package io.github.nodecanvasusages.rider

import com.jetbrains.rider.settings.simple.SimpleOptionsPage

/**
 * Frontend host for the backend options page `NodeCanvasUsagesSettings`
 * (ReSharperPlugin.NodeCanvasUsages.Rider.Options.NodeCanvasOptionsPage); Rider shows backend pages only through such a
 * registered configurable.
 */
class NodeCanvasOptionsPage : SimpleOptionsPage("NodeCanvas Usages", "NodeCanvasUsagesSettings") {
    override fun getId(): String = "preferences.nodeCanvasUsages"
}
